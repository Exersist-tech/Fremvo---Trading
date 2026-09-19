# Domain Model

All types below live in exchange-neutral projects (`Trading.Domain`,
`Trading.Application`, `Trading.Strategies`, `Trading.Risk`,
`Trading.Backtesting`) unless marked otherwise. None of these reference
Binance types, EF Core, Azure SDKs, or ASP.NET Core.

## 1. Identity & access

- `User` (id, invitation reference, email, display name, locale, time zone,
  reporting currency, roles, MFA status, status: Active/Suspended).
- `Invitation` (code, issuer, max uses, used count, expiry, status).
- `Role` (enum-like value object: `TrialUser`, `User`, `SupportAgent`,
  `RiskOfficer`, `Administrator`, `SystemService`).
- `AuditEvent` (actor, action, target, timestamp UTC, correlation id,
  before/after summary — never includes secret values).

## 2. Exchange accounts & secrets

- `ExchangeAccount` (id, owner `UserId`, exchange identifier e.g.
  `Binance`, environment: `Testnet`/`Live`, market type capability flags:
  Spot/Futures, status: `PendingValidation`/`Active`/`Suspended`/`Revoked`,
  granted-permission summary, `SecretReferenceId`).
- `SecretReference` (opaque pointer to a Key Vault secret — never the
  secret value itself; includes vault URI/name and version, not the key).
- `ApiPermissionSnapshot` (validated permissions returned by exchange at
  connection time: read, trade, no-withdraw confirmed; last validated at).

`Trading.Domain` never holds a raw API key/secret; only
`Trading.Infrastructure.Secrets` talks to Key Vault, returning a short-lived
credential handle to the connector at call time.

## 3. Market data

- `Symbol` (exchange-neutral trading pair, e.g. base/quote asset codes,
  the exchange it's sourced from, and its `SymbolFilterSet`).
- `SymbolFilterSet` (price tick size, quantity step size, minimum notional,
  min/max quantity — exchange-neutral value object populated per exchange).
- `CandleInterval` (enum: `OneMinute`, `FiveMinutes`, `TenMinutes`,
  `FifteenMinutes`, `ThirtyMinutes`, `OneHour`, `FourHours`, `OneDay`).
- `Candle` (symbol, interval, open time UTC, close time UTC, OHLCV as
  `decimal`, `IsDerived` flag, `IsClosed` flag, source sequence/id for
  duplicate detection).
- `DataQualityFlag` (enum: `Missing`, `Duplicate`, `Stale`, `Late`,
  `OutOfOrder`) attached to ingestion results for observability and to
  block signal generation on affected data.

Derived candles (e.g. 10-minute from ten closed 1-minute candles) are
always explicitly marked `IsDerived = true` and only built from `IsClosed`
source candles.

## 4. Indicators

- `IIndicator<TResult>` — pure function over an ordered candle series,
  side-effect free, deterministic, unit-testable without I/O.
- Examples: `SimpleMovingAverage`, `ExponentialMovingAverage`, `RSI`,
  `MACD`, `BollingerBands`, `ATR`. Each documents its minimum lookback and
  warm-up period.

## 5. Market scanner

- `ScanCriterion` (indicator/condition expression over a symbol + interval,
  e.g. "RSI(14) < 30 on 1h").
- `ScanRequest` (universe of symbols, criteria set, schedule).
- `ScanResult` (ranked candidates, evaluated-at UTC, criteria matched).

## 6. Strategies

- `StrategyTemplateId` — identifier of an approved, platform-authored
  strategy (never user-supplied code).
- `StrategyParameterDefinition` (name, type, min, max, default, step) —
  defines the *approved range*; any parameter set is validated against
  this before use.
- `StrategyParameterSet` — a concrete, validated set of parameter values
  for one strategy instance.
- `IStrategy` — pure decision function: given a stream of closed
  `MarketEvent`s and current `StrategyState`, produces zero or more
  `StrategyDecision`s and an updated `StrategyState`. No I/O, no exchange
  access, fully replayable.
- `StrategyState` — strategy-owned, serializable state (e.g. indicator
  accumulators, position bias) scoped per experiment worker.

## 7. Trading pipeline entities

- `MarketEvent` — a closed candle or normalized tick made available to
  strategies; carries data-quality flags so downstream stages can refuse
  stale input.
- `StrategyDecision` — strategy's intended action (e.g. `EnterLong`,
  `ExitShort`, `NoAction`) with rationale and confidence metadata, not yet
  an order.
- `TradeIntent` — a decision translated into a candidate trade (symbol,
  side, requested quantity/price basis, market/limit, reduce-only flag)
  before risk evaluation.
- `RiskEvaluation` — outcome of the risk engine on a `TradeIntent`:
  `Approved`, `Rejected(reason)`, or `Modified(newIntent, reason)`; a
  modification is always explicit and audited, never silent.
- `ExecutionCommand` — the risk-approved instruction to place/cancel an
  order, carrying an idempotent `ClientOrderId`.
- `IExecutionAdapter` (Application port) — implemented by
  `PaperExecutionAdapter` and by Binance Spot/Futures adapters; identical
  contract for both.
- `ExecutionResult` — raw outcome from the adapter (accepted, rejected,
  unknown/timeout) before reconciliation.
- `ReconciliationRecord` — comparison of expected vs. actual order/
  position state after an execution attempt or on recovery; required
  before further submissions for the same order/position.
- `PortfolioUpdate` — resulting change to balances/positions after a
  reconciled execution.

## 8. Orders & positions (state machines — see below)

- `Order` (id, client order id, exchange order id (nullable until known),
  account, symbol, market type Spot/Futures, side, type, quantity, price,
  reduce-only flag, status, timestamps UTC).
- `OrderStatus` (state machine, §9).
- `Position` (account, symbol, market type, side (for Futures: long/short),
  quantity, average entry price, unrealized/realized P&L, margin info for
  Futures, status).
- `PositionStatus` (state machine, §10).
- `Fill` (order id, quantity, price, fee, fee asset, timestamp UTC).

## 9. Order state machine

```
Created -> PendingSubmit -> Submitted -> {PartiallyFilled -> Submitted|Filled}
Submitted -> Filled
Submitted -> Rejected
Submitted -> CancelPending -> Cancelled
Submitted -> Unknown (ambiguous/timeout) -> Reconciled -> {Submitted|Filled|Cancelled|Rejected}
```

Rules:
- `Unknown` is a first-class state, not an error swallowed as `Rejected` or
  retried as `Created`. Only `Reconciliation` may move an order out of
  `Unknown`.
- No transition skips `Reconciled` after `Unknown`.
- `Cancelled`, `Filled`, `Rejected` are terminal.

## 10. Position state machine

```
Flat -> Opening -> Open -> {Increasing -> Open, ReduceOnly -> Reducing -> Open|Flat}
Open -> Closing -> Flat
Any -> Liquidated (Futures only, exchange-reported)
```

Rules:
- Futures positions additionally track `MarginMode`, `Leverage` (never
  auto-increased), `LiquidationPrice`, `MarkPrice`, `FundingAccrued`.
- Increasing exposure is blocked whenever required market/account data is
  stale (see Risk engine).
- `ReduceOnly`/`CloseOnly` modes constrain which transitions are legal
  regardless of strategy intent.

## 11. Risk engine

- `RiskLimit` (scope: Platform/User/Account/Strategy; max position size,
  max leverage, max daily loss, max order rate, max open positions).
- `RiskLimitHierarchy` — platform ceilings always win; user-configured
  limits may only be equal to or stricter than the platform ceiling for
  their scope.
- `HaltSwitch` (scope: Global/User/Account/Strategy; states:
  `Active`/`Halted`; reason, actor, timestamp).
- `TradingModeFlags` — `CloseOnly`, `ReduceOnly`, `LiveTradingEnabled`,
  `LeverageTradingEnabled` (all default false/most-restrictive).
- `StalenessPolicy` — max age for price/account data before blocking new
  or exposure-increasing orders.
- `DuplicateOrderGuard` — enforced via idempotent `ClientOrderId` plus a
  short-lived dedupe record.

## 12. Backtesting & optimization

- `HistoricalDataset` (symbol/interval range, immutable, versioned).
- `DatasetSplit` — `Training`, `Validation`, `Holdout`, `WalkForwardFold`
  (ordered, non-overlapping by time; `Holdout` is never used for parameter
  selection, enforced by an explicit guard in the optimization pipeline).
- `BacktestConfiguration` (dataset split reference, strategy template +
  parameter set, fee model, slippage model, starting fake balance, random
  seed).
- `FeeModel` / `SlippageModel` — pure functions producing deterministic,
  reproducible costs given the same seed.
- `BacktestResult` (equity curve, trade log, metrics: return, drawdown,
  win rate, Sharpe-like ratios — all clearly labeled as historical
  simulation, never a promise of future results).
- `OptimizationRun` (parameter search space, objective metric, evaluated on
  Training/Validation only; best candidate re-verified once on Holdout and
  never re-tuned afterward; `WalkForwardFold` results reported alongside).

## 13. Experiment workers

- `ExperimentWorker` (id 1..10 or GUID, owner scope: platform/user,
  mode: `Paper`/`Live` (Live gated), strategy template + parameter set,
  independent `Ledger` (fake or real-linked balances), independent
  `StrategyState`, independent `RandomSeed`, status: `Running`/`Stopped`/
  `Faulted`, isolation boundary: one worker's fault never halts others).

## 14. Reporting & entitlements

- `ReportingProfile` (user id, language, time zone, reporting currency,
  optional country profile — all optional/pluggable, never hard-coded).
- `TransactionReport` (period, entries derived from Fills/PortfolioUpdate
  history, export format, explicit "not tax/legal/financial advice"
  disclaimer).
- `Plan` (name, feature flags: max experiment workers, live trading
  eligibility, futures eligibility).
- `Entitlement` (user id, plan id, trial expiry if applicable, status).

## 15. Key interfaces summary (Application layer ports)

| Interface | Implemented by |
|---|---|
| `IExchangeConnector`, `IMarketDataSource`, `ISpotOrderGateway`, `IFuturesOrderGateway`, `IAccountGateway` | `Trading.Exchanges.Binance` (later: other exchanges) |
| `ISecretStore` | `Trading.Infrastructure.Secrets` (Key Vault) |
| `ICandleRepository`, `IOrderRepository`, `IPositionRepository`, `IAuditEventRepository`, etc. | `Trading.Infrastructure.Data` (EF Core / Azure SQL) |
| `IRealtimeCache` | `Trading.Infrastructure.Cache` (Redis) |
| `IEventBus` | `Trading.Infrastructure.Messaging` (Azure Service Bus) |
| `IExecutionAdapter` | `PaperExecutionAdapter`, Binance Spot/Futures adapters |
| `IStrategy` | Each approved strategy template in `Trading.Strategies` |
| `IIndicator<T>` | Each indicator in `Trading.Indicators` |
