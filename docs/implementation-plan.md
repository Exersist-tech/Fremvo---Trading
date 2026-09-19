# Implementation Plan

This plan breaks delivery into small, sequential phases. Each phase is
further split into small tasks (each independently completable, buildable,
testable, and reviewable — matching the "Working method" in
`.github/copilot-instructions.md`). No phase begins until its dependencies
are done and reviewed. Live trading (Phase 9) and leveraged futures
(Phase 12) are deliberately late. `docs/implementation-status.md` tracks
per-task status; every task starts as **Not started**.

Every phase below documents: Purpose, Features, Projects affected, Main
entities/interfaces, Database changes, Security considerations, Trading and
financial risks, Required tests, Acceptance criteria, Dependencies, and
Explicitly excluded work.

---

## Phase 0 — Solution foundations

1. **Purpose**: Establish the solution skeleton, coding standards, and CI
   baseline so every later phase builds on a consistent, tested foundation.
2. **Features**: Empty `Trading.sln` with the project layout from
   `docs/architecture.md`; shared `.editorconfig`/analyzers; nullable
   reference types + warnings-as-errors baseline; GitHub Actions build+test
   workflow; architecture tests proving Domain purity from day one.
3. **Projects affected**: All initial project shells (`Trading.Domain`,
   `Trading.Application`, `Trading.Web`, and their test projects); others
   are added as later phases need them.
4. **Main entities and interfaces**: None yet — this phase is scaffolding
   only.
5. **Database changes**: None.
6. **Security considerations**: Establish secret-scanning in CI (e.g.
   `gitleaks`/GitHub secret scanning) before any real code lands.
7. **Trading and financial risks**: None (no trading logic exists yet).
8. **Required tests**: A placeholder architecture test asserting
   `Trading.Domain` has zero references to Azure/EF/HTTP/Binance/UI
   assemblies (fails fast if violated later).
9. **Acceptance criteria**: `dotnet build` and `dotnet test` succeed in CI
   on a clean checkout; architecture test passes.
10. **Dependencies on earlier phases**: None (first phase).
11. **Explicitly excluded work**: Any domain, identity, or trading code.

Tasks:
- 0.1 Create solution + empty project shells + solution-level analyzers.
- 0.2 Add architecture test project + Domain-purity rule.
- 0.3 Add GitHub Actions CI workflow (build, test, architecture tests).

---

## Phase 1 — Identity, invitations, roles, audit log

1. **Purpose**: Allow invited users to register and authenticate, and
   establish the immutable audit trail used by every later sensitive
   action.
2. **Features**: Invitation issuance/redemption, registration, login, MFA
   (mandatory for Administrator), roles, `AuditEvent` logging
   infrastructure.
3. **Projects affected**: `Trading.Domain` (User/Invitation/Role/
   AuditEvent), `Trading.Application` (use cases), `Trading.Infrastructure.
   Data` (EF Core mapping), `Trading.Web` (Blazor auth UI + identity
   endpoints).
4. **Main entities and interfaces**: `User`, `Invitation`, `Role`,
   `AuditEvent`, `IUserRepository`, `IInvitationRepository`,
   `IAuditEventWriter`.
5. **Database changes**: `Users`, `Roles`, `UserRoles`, `Invitations`,
   `AuditEvents` (see `docs/database-plan.md` §1).
6. **Security considerations**: Password policy, MFA enforcement for
   Administrator, invitation single-use/expiry, authorization scaffolding
   (claims/roles), audit of login/role/invitation events.
7. **Trading and financial risks**: None yet (no exchange/trading
   concepts introduced).
8. **Required tests**: Registration requires valid invitation; expired/
   exhausted invitations rejected; Administrator actions blocked without
   MFA; audit events recorded for login, invitation issue/redeem, role
   change.
9. **Acceptance criteria**: A user can be invited, register, log in;
   Administrator role requires MFA; all sensitive actions produce
   `AuditEvent` rows.
10. **Dependencies on earlier phases**: Phase 0.
11. **Explicitly excluded work**: Exchange accounts, trading, plans/
    billing.

Tasks:
- 1.1 Domain entities + value objects (User, Invitation, Role, AuditEvent).
- 1.2 EF Core mapping + migration for identity tables.
- 1.3 Invitation issuance + redemption use cases.
- 1.4 Registration/login (ASP.NET Core Identity integration) in Blazor Web.
- 1.5 MFA enrollment/enforcement for Administrator role.
- 1.6 Audit event writer + audit log viewer (Administrator-only UI).

---

## Phase 2 — Exchange account connection (Binance, secrets)

1. **Purpose**: Let a user connect a Binance account (Testnet first) with
   API keys stored securely, never granting withdrawal capability.
2. **Features**: Add exchange account, validate API key permissions
   (read+trade only, no withdraw), store secret reference via Key Vault,
   revoke/disconnect account.
3. **Projects affected**: `Trading.Exchanges.Abstractions`,
   `Trading.Exchanges.Binance`, `Trading.Infrastructure.Secrets`,
   `Trading.Application`, `Trading.Web`.
4. **Main entities and interfaces**: `ExchangeAccount`, `SecretReference`,
   `ApiPermissionSnapshot`, `IExchangeConnector`, `IAccountGateway`,
   `ISecretStore`.
5. **Database changes**: `ExchangeAccounts`, `SecretReferences` (see
   `docs/database-plan.md` §2).
6. **Security considerations**: Key Vault + Managed Identity only; reject
   keys with withdraw permission; separate Testnet/Live Key Vaults; secret
   values never logged or returned to the browser.
7. **Trading and financial risks**: None yet (no orders placed); risk is
   entirely about secret handling correctness.
8. **Required tests**: Reject a key with withdraw permission; secret value
   never appears in any API response/log (serialization test); connection
   validation flow against Binance Testnet (or recorded responses).
9. **Acceptance criteria**: A user can connect a Binance Testnet account;
   the platform stores only a `SecretReference`; disconnect revokes access
   cleanly.
10. **Dependencies on earlier phases**: Phases 0–1.
11. **Explicitly excluded work**: Live (production) credentials, order
    placement, market data.

Tasks:
- 2.1 `Trading.Exchanges.Abstractions` ports + neutral value objects.
- 2.2 `Trading.Infrastructure.Secrets` Key Vault-backed `ISecretStore`.
- 2.3 `Trading.Exchanges.Binance` account/permission validation gateway.
- 2.4 Application use cases: connect/validate/disconnect exchange account.
- 2.5 Blazor UI for connecting/managing exchange accounts.

---

## Phase 3 — Market data ingestion, storage, indicators, charts

1. **Purpose**: Continuously ingest Binance market data, normalize it into
   platform candle intervals, store it reliably, compute indicators, and
   render charts.
2. **Features**: Historical + streaming candle ingestion, derived 10-minute
   candles, data-quality detection (missing/duplicate/stale/late/out-of-
   order), indicator library, chart UI.
3. **Projects affected**: `Trading.MarketData`, `Trading.Indicators`,
   `Trading.Exchanges.Binance` (market data source), `Trading.
   Infrastructure.Data`, `Trading.Workers.MarketData`, `Trading.Web`
   (charts).
4. **Main entities and interfaces**: `Symbol`, `SymbolFilterSet`,
   `CandleInterval`, `Candle`, `DataQualityFlag`, `IMarketDataSource`,
   `ICandleRepository`, `IIndicator<T>`.
5. **Database changes**: `Symbols`, `Candles` (see `docs/database-plan.md`
   §3).
6. **Security considerations**: Market data is not sensitive, but the
   ingestion worker's exchange credentials (if any, for higher rate
   limits) follow the same secret-handling rules as Phase 2.
7. **Trading and financial risks**: Incorrect candle data would corrupt
   every downstream decision; data-quality flags must block signal
   generation on bad data (enforced here even though strategies come
   later).
8. **Required tests**: Derived-candle correctness (10 closed 1-minute →
   1 derived 10-minute, flagged `IsDerived`); duplicate/out-of-order/late
   detection; indicator reference-value tests; no-look-ahead guard (cannot
   read an unclosed candle).
9. **Acceptance criteria**: Historical and live candles for a configured
   symbol/interval set are ingested, stored, flagged for quality issues,
   and rendered on a chart with at least two indicators.
10. **Dependencies on earlier phases**: Phases 0–2 (uses `IMarketDataSource`
    from the Binance connector).
11. **Explicitly excluded work**: Scanner, strategies, backtesting, any
    trading.

Tasks:
- 3.1 `Symbol`/`Candle`/`CandleInterval` domain model + repository ports.
- 3.2 Binance historical candle fetch + normalization.
- 3.3 Binance streaming candle ingestion worker.
- 3.4 Data-quality detection (missing/duplicate/stale/late/out-of-order).
- 3.5 Derived 10-minute candle builder from closed 1-minute candles.
- 3.6 Core indicator library (SMA, EMA, RSI, MACD, Bollinger, ATR).
- 3.7 Blazor charting UI with indicator overlays.

---

## Phase 3B — Market universe and instrument eligibility

Full detail: `docs/market-universe.md`.

1. **Purpose**: Decide which instruments may be used, for what purpose,
   and in which trading mode — the gate in front of research, backtesting,
   paper trading, and later execution.
2. **Features**: Binance exchange-information catalogue sync; the 50-pair
   USDT research seed (all `Tracked` only); asset classification and
   permanent exclusions; instrument state model with ten states; per
   purpose/timeframe/product/mode eligibility gates; rolling liquidity,
   spread, slippage and data-health metrics; scheduled recalculation;
   newly-listed restricted state; degradation behaviour.
3. **Projects affected**: `Trading.Domain` (states, gates, metrics),
   `Trading.MarketData`, `Trading.Exchanges.Binance` (catalogue mapping
   only), `Trading.Infrastructure`, `Trading.Workers.MarketData`,
   `Trading.Web`.
4. **Main entities and interfaces**: `Instrument`, `InstrumentState`,
   `InstrumentEligibilityGrant`, `AssetClass`, `InstrumentMetrics`,
   `EligibilityGate`, `EligibilityEvaluation`,
   `IInstrumentCatalogueSource`, `IInstrumentEligibilityEvaluator`,
   `IInstrumentMetricsRepository`.
5. **Database changes**: `Instruments`, `InstrumentSeed`,
   `InstrumentMetrics`, `InstrumentEligibilityEvaluations`,
   `InstrumentStateTransitions` (see `docs/database-plan.md` §4A).
6. **Security considerations**: Catalogue sync is read-only public market
   data and must use no API secret. Binance DTOs must not reach
   `Trading.Domain`. Eligibility changes are audited. Only an
   administrator may edit the seed or thresholds, and configured
   thresholds may only be stricter than the platform floors.
7. **Trading and financial risks**: An over-permissive gate is the most
   likely route to trading an illiquid instrument. Single-reading volume
   selection, stale evidence trusted as current, and silent dropping of a
   missing symbol are the specific failure modes to prevent. Degradation
   must block new entries and increases while still permitting validated
   reduction, and must require reconciliation before recovery.
8. **Required tests**: The 29 tests listed in `docs/market-universe.md`
   §10.
9. **Acceptance criteria**: `docs/market-universe.md` §11.
10. **Dependencies on earlier phases**: Phase 3 (symbols, candles, data
    quality). Phase 2 supplies the connector, but catalogue sync needs no
    user credentials.
11. **Explicitly excluded work**: Any order placement; Futures contract
    eligibility beyond state definitions; automatic promotion to a live
    grant; withdrawals (permanently excluded).

Tasks:
- 3B.1 `Instrument`, `InstrumentState`, `AssetClass` domain + exclusions.
- 3B.2 Eligibility grant model (per purpose/timeframe/product/mode).
- 3B.3 `EligibilityGate` evaluation engine + explainable evaluation record.
- 3B.4 `InstrumentMetrics` rolling/median measurement model + repository.
- 3B.5 Binance exchange-information catalogue source + neutral mapping.
- 3B.6 50-pair seed as versioned configuration (all `Tracked` only).
- 3B.7 Newly-listed restricted state + evidence-age expiry.
- 3B.8 Degradation rules (block entries/increases, allow reduction).
- 3B.9 Recalculation worker (scheduled + event-triggered).
- 3B.10 Administrator instrument/eligibility UI with gate explanations.

---

## Phase 4 — Automatic market scanner

1. **Purpose**: Continuously screen a symbol universe against configured
   indicator/volume/volatility criteria to surface candidates.
2. **Features**: Scan criteria configuration, scheduled scanning worker,
   ranked results view.
3. **Projects affected**: New scanner logic within `Trading.MarketData` (or
   a dedicated `Trading.Scanner` project), `Trading.Workers.Scanner`,
   `Trading.Web`.
4. **Main entities and interfaces**: `ScanCriterion`, `ScanRequest`,
   `ScanResult`.
5. **Database changes**: `ScanRequests`, `ScanResults` (see
   `docs/database-plan.md` §4).
6. **Security considerations**: Scanner is read-only over market data; no
   new secret or trading surface.
7. **Trading and financial risks**: None directly (no orders); scanner
   output must not be presented as a trading recommendation/guarantee.
8. **Required tests**: Criteria evaluation correctness against known
   candle fixtures; scheduling triggers scans at configured cadence;
   results ranked deterministically for identical input.
9. **Acceptance criteria**: A configured scan runs on schedule and produces
   ranked, explainable results in the UI.
10. **Dependencies on earlier phases**: Phase 3 (candles + indicators).
11. **Explicitly excluded work**: Strategies acting on scanner output,
    backtesting, trading.

Tasks:
- 4.1 `ScanCriterion`/`ScanRequest`/`ScanResult` domain + repositories.
- 4.2 Criteria evaluation engine (reuses `Trading.Indicators`).
- 4.3 `Trading.Workers.Scanner` scheduled execution.
- 4.4 Blazor scanner results UI.

---

## Phase 5 — Approved strategy templates + backtesting engine

1. **Purpose**: Provide a small set of platform-authored, parameterized
   strategies and a deterministic backtesting engine to evaluate them
   historically.
2. **Features**: `IStrategy` contract, 2–3 initial approved templates with
   bounded parameter ranges, backtest engine with fee/slippage/filter/
   partial-fill modeling, backtest result reporting.
3. **Projects affected**: `Trading.Strategies`, `Trading.Backtesting`,
   `Trading.Application`, `Trading.Web` (backtest configuration/results
   UI).
4. **Main entities and interfaces**: `StrategyTemplateId`,
   `StrategyParameterDefinition`, `StrategyParameterSet`, `IStrategy`,
   `StrategyState`, `HistoricalDataset`, `BacktestConfiguration`,
   `FeeModel`, `SlippageModel`, `BacktestResult`.
5. **Database changes**: `StrategyTemplates`, `StrategyParameterDefinitions`,
   `StrategyParameterSets`, `HistoricalDatasets`, `BacktestRuns`,
   `BacktestTrades` (see `docs/database-plan.md` §5–6).
6. **Security considerations**: Strategy templates are platform-authored
   only; no code-upload surface is ever exposed to users; parameter sets
   are validated against `StrategyParameterDefinition` bounds before use.
7. **Trading and financial risks**: Backtests must never claim guaranteed
   profit; must account for fees/spread/slippage/filters/partial fills;
   must not read unclosed candles (no look-ahead bias).
8. **Required tests**: Determinism/reproducibility test with fixed seed;
   fee/slippage application correctness; parameter-range validation
   rejects out-of-bounds sets; no-look-ahead guard test.
9. **Acceptance criteria**: A user can configure and run a backtest for an
   approved strategy over a historical range and see a reproducible,
   itemized result (trades, equity curve, fees).
10. **Dependencies on earlier phases**: Phase 3 (market data + indicators).
11. **Explicitly excluded work**: Optimization/parameter search (Phase 6),
    live/paper execution (Phase 7+), experiment workers.

Tasks:
- 5.1 `IStrategy`/`StrategyState`/parameter-definition domain model.
- 5.2 First approved strategy template (e.g. trend-following) + tests.
- 5.3 Second approved strategy template (e.g. mean-reversion) + tests.
- 5.4 `HistoricalDataset` storage/versioning.
- 5.5 Backtest engine core loop (event-driven, closed-candle only).
- 5.6 Fee model, slippage model, exchange filter application.
- 5.7 Backtest result reporting + Blazor UI.

---

## Phase 5B — Ten approved research strategy families

Full detail: `docs/strategy-research-plan.md`.

1. **Purpose**: Define and implement the ten falsifiable research
   templates, their approval lifecycle, and the rejection gates that stop
   an overfit configuration from progressing.
2. **Features**: Regime/signal/execution timeframe separation; versioned
   `SessionProfile` using IANA identifiers; deterministic regime
   classifier; strategy approval state machine; rejection gates;
   instrument-and-timeframe approval requirements; ten-worker experiment
   groups A/B/C.
3. **Projects affected**: `Trading.Strategies`, `Trading.Indicators`,
   `Trading.Backtesting`, `Trading.Application`, `Trading.Web`.
4. **Main entities and interfaces**: `StrategyApproval`,
   `StrategyApprovalState`, `StrategyVersion`, `SessionProfile`,
   `RegimeState`, `IRegimeClassifier`, `IRejectionGate`,
   `ExperimentGroup`.
5. **Database changes**: `StrategyApprovals`, `StrategyVersions`,
   `SessionProfiles`, `RegimeEvaluations`, `RejectionGateResults` (see
   `docs/database-plan.md` §5A).
6. **Security considerations**: Templates remain platform-authored; no
   user code upload. Approval transitions require an audited human
   approver and cannot be performed by an automated process.
7. **Trading and financial risks**: The dominant risk is
   optimization-selection bias — picking the highest-return worker.
   Selection by total profit must be impossible by design. Mean reversion
   must not permit unrestricted averaging down. Ensemble agreement must
   not raise exposure above the platform maximum. No profit claims.
8. **Required tests**: The 16 tests listed in
   `docs/strategy-research-plan.md` §9.
9. **Acceptance criteria**: `docs/strategy-research-plan.md` §10.
10. **Dependencies on earlier phases**: Phase 3 (candles, indicators),
    Phase 3B (instrument eligibility), Phase 5 (strategy contract,
    backtest engine, cost models).
11. **Explicitly excluded work**: Live or testnet order placement;
    automatic approval promotion; withdrawals.

Tasks:
- 5B.1 Strategy approval state machine + versioned, immutable approvals.
- 5B.2 Strategy approval requirements (instrument class, history,
  liquidity, spread, slippage, timeframes, product type, modes).
- 5B.3 Regime/signal/execution timeframe separation in the strategy
  contract.
- 5B.4 Rejection-gate engine (§2.3) with a recorded result per gate.
- 5B.5 Family 1 — multi-timeframe EMA trend continuation.
- 5B.6 Family 2 — Donchian breakout ensemble.
- 5B.7 Family 3 — Bollinger mean reversion with ranging-regime filter.
- 5B.8 Family 4 — RSI pullback within a higher-timeframe trend.
- 5B.9 Family 5 — MACD and volume-confirmed trend acceleration.
- 5B.10 Family 6 — volatility-compression breakout.
- 5B.11 Family 7 — cross-sectional momentum rotation (survivorship-aware).
- 5B.12 Family 8 — relative-strength pullback rotation.
- 5B.13 `SessionProfile` + IANA/DST-safe session engine.
- 5B.14 Family 9 — session-conditioned breakout vs no-session baseline.
- 5B.15 Deterministic, versioned regime classifier.
- 5B.16 Family 10 — regime-switching ensemble.
- 5B.17 Derived 4-day candle interval (documented UTC boundary), optional.
- 5B.18 Experiment groups A/B/C wiring for the ten workers.

---

## Phase 6 — Optimization (train/validation/holdout/walk-forward)

1. **Purpose**: Search approved parameter ranges responsibly, preventing
   holdout leakage and overfitting.
2. **Features**: Dataset splitting (training/validation/holdout/walk-
   forward folds), parameter search over `StrategyParameterDefinition`
   bounds, one-time holdout verification, walk-forward reporting.
3. **Projects affected**: `Trading.Optimization`, `Trading.Backtesting`
   (reused), `Trading.Web` (optimization UI).
4. **Main entities and interfaces**: `DatasetSplit`, `OptimizationRun`,
   objective-metric abstraction.
5. **Database changes**: `DatasetSplits`, `OptimizationRuns` (see
   `docs/database-plan.md` §6).
6. **Security considerations**: None beyond existing (no new secret
   surface); optimization results must not be exposed as guarantees.
7. **Trading and financial risks**: A guard must make it structurally
   impossible to use Holdout data for parameter selection or to re-tune
   after Holdout verification; walk-forward folds must be time-ordered and
   non-overlapping to avoid future leakage.
8. **Required tests**: Split non-overlap/time-order test; holdout-guard
   test (attempt to reuse holdout for tuning must fail); walk-forward fold
   evaluation correctness.
9. **Acceptance criteria**: An optimization run selects a best parameter
   set from training/validation, verifies once on holdout, and reports
   walk-forward results, all reproducible.
10. **Dependencies on earlier phases**: Phase 5.
11. **Explicitly excluded work**: Any live/paper execution.

Tasks:
- 6.1 `DatasetSplit` domain + time-ordered, non-overlapping construction.
- 6.2 Parameter search algorithm (e.g. grid/random search) over bounds.
- 6.3 Holdout one-time-verification guard.
- 6.4 Walk-forward evaluation + reporting.
- 6.5 Blazor optimization configuration/results UI.

---

## Phase 7 — Paper trading + isolated experiment workers

1. **Purpose**: Run approved strategies against live market data with fake
   funds through the exact same pipeline that will later place real
   orders, across up to 10 isolated experiment workers.
2. **Features**: `PaperExecutionAdapter`, experiment worker lifecycle
   (create/start/stop), isolated ledger/state/random-seed per worker,
   fault isolation.
3. **Projects affected**: `Trading.Application` (pipeline orchestration),
   `Trading.Risk` (initial minimal checks reused from Phase 8 groundwork),
   `Trading.Workers.Experiments`, `Trading.Web` (experiment dashboard).
4. **Main entities and interfaces**: `MarketEvent`, `StrategyDecision`,
   `TradeIntent`, `ExecutionCommand`, `IExecutionAdapter`,
   `PaperExecutionAdapter`, `ExperimentWorker`, `ExperimentLedger`,
   `ExperimentStrategyState`.
5. **Database changes**: `ExperimentWorkers`, `ExperimentLedgers`,
   `ExperimentStrategyStates`, plus pipeline tables `MarketEventsProcessed`,
   `StrategyDecisions`, `TradeIntents`, `ExecutionCommands`, `Orders`
   (paper-only rows), `Fills`, `PortfolioUpdates` (see
   `docs/database-plan.md` §7–8).
6. **Security considerations**: Paper workers use no real exchange
   secrets; ensure paper vs. live is unambiguous everywhere in data and UI
   to prevent later confusion.
7. **Trading and financial risks**: Establishes the full pipeline shape
   (MarketEvent → … → AuditEvent) so live trading later reuses proven code;
   a fault in one worker must not affect the other nine (tested
   explicitly); paper results must not be presented as predictive of live
   performance.
8. **Required tests**: End-to-end paper pipeline test per event; worker
   isolation test (fault in one doesn't affect others); idempotent
   client-order-id behavior even in paper mode (groundwork for Phase 8);
   at-most-10-workers constraint test.
9. **Acceptance criteria**: Up to 10 concurrently running paper experiment
   workers, each with independent state, execute approved strategies
   against live market data, producing auditable paper orders/positions.
10. **Dependencies on earlier phases**: Phases 3, 5 (and 6 optionally for
    parameter selection).
11. **Explicitly excluded work**: Real exchange order placement, risk
    engine limits/halts (Phase 8), reconciliation against a real exchange.

Tasks:
- 7.1 Pipeline entities (`MarketEvent`…`ExecutionCommand`) + repositories.
- 7.2 `PaperExecutionAdapter` implementing `IExecutionAdapter`.
- 7.3 `ExperimentWorker` lifecycle + isolation boundary (fault handling).
- 7.4 `Trading.Workers.Experiments` hosting up to 10 workers.
- 7.5 Experiment dashboard (Blazor): create/start/stop, view ledger/state.

---

## Phase 8 — Risk engine, halts, idempotency, reconciliation

1. **Purpose**: Add the mandatory safety layer required before any real
   money is at risk: limits, halts, staleness protection, duplicate-order
   protection, and reconciliation.
2. **Features**: `RiskEvaluation` gate wired into the pipeline for both
   paper and (future) live; halt switches at all scopes; close-only/
   reduce-only modes; staleness policy; duplicate-order guard; order/
   position reconciliation logic (including `Unknown` state resolution).
3. **Projects affected**: `Trading.Risk`, `Trading.Application`
   (pipeline), `Trading.Workers.Execution`, `Trading.Web` (admin/risk
   dashboard, emergency stop control).
4. **Main entities and interfaces**: `RiskLimit`, `RiskLimitHierarchy`,
   `HaltSwitch`, `TradingModeFlags`, `StalenessPolicy`,
   `DuplicateOrderGuard`, `ReconciliationRecord`, order/position state
   machines fully implemented.
5. **Database changes**: `RiskLimits`, `HaltSwitches`, `TradingModeFlags`,
   `DuplicateOrderGuards`, `ReconciliationRecords` (see
   `docs/database-plan.md` §9); `Orders`/`Positions` gain full status
   columns per the state machines in `docs/domain-model.md` §9–10.
6. **Security considerations**: Halt/limit changes are Administrator/
    RiskOfficer-only and audited; emergency stop must be reachable
    independent of a partially degraded web UI.
7. **Trading and financial risks**: This phase directly implements the
   platform's core safety guarantees — platform ceilings cannot be
   overridden by user config; stale data blocks new/increasing exposure;
   `Unknown` order status can only be resolved by reconciliation, never
   blind retry; duplicate submission is structurally prevented via
   idempotent client order IDs.
8. **Required tests**: Full state-machine transition tests (legal/illegal);
   staleness-blocks-new-orders test; halt-at-every-scope tests;
   platform-ceiling-cannot-be-overridden test; duplicate-submission test;
   reconciliation-required-before-resubmit test.
9. **Acceptance criteria**: Paper trading pipeline now runs through full
   risk evaluation and reconciliation; all halt switches and safety modes
   are demonstrably effective in tests; emergency stop halts all new
   order flow platform-wide.
10. **Dependencies on earlier phases**: Phase 7.
11. **Explicitly excluded work**: Real exchange order placement (Phase 9+).

Tasks:
- 8.1 `RiskLimit`/`RiskLimitHierarchy` + platform-ceiling enforcement.
- 8.2 `HaltSwitch` (all scopes) + emergency-stop control path.
- 8.3 `TradingModeFlags` (close-only/reduce-only/live/leverage toggles).
- 8.4 `StalenessPolicy` enforcement in the pipeline.
- 8.5 `DuplicateOrderGuard` + idempotent `ClientOrderId` generation.
- 8.6 Order/Position state machines (full implementation + persistence).
- 8.7 Reconciliation service (including `Unknown` resolution + recovery).
- 8.8 Admin/risk dashboard UI (halts, limits, emergency stop).

---

## Phase 9 — Binance Spot testnet live-path trading

1. **Purpose**: Prove the full pipeline against real Binance Testnet API
   calls (not simulation) before any real money is involved.
2. **Features**: `BinanceSpotExecutionAdapter` implementing
   `IExecutionAdapter` against Binance Spot Testnet; order placement,
   cancellation, status query, fill retrieval; reconciliation against real
   (testnet) exchange responses.
3. **Projects affected**: `Trading.Exchanges.Binance`, `Trading.Workers.
   Execution`, `Trading.Application`.
4. **Main entities and interfaces**: `ISpotOrderGateway` (Binance
   implementation), `BinanceSpotExecutionAdapter`.
5. **Database changes**: None beyond Phase 8's `Orders`/`Fills`/
   `ReconciliationRecords` (now populated with real testnet exchange order
   IDs).
6. **Security considerations**: Uses Testnet `ExchangeAccount`/
   `SecretReference` only; Live Key Vault remains inaccessible from this
   code path in Test/Staging environments.
7. **Trading and financial risks**: This is the first phase where real
   (sandbox) exchange responses, latency, and error codes are handled;
   focus on correct handling of partial fills, rejections, and unknown-
   status timeouts exactly as designed in Phase 8.
8. **Required tests**: Integration tests against Binance Testnet (or
   recorded fixtures) for place/cancel/query/fill; simulated timeout →
   `Unknown` → reconciliation resolution; filter/tick/step rejection
   handling (never silently altering the order).
9. **Acceptance criteria**: An experiment worker in `Live` mode against
   Binance Testnet places, fills, and reconciles real (sandbox) Spot
   orders end-to-end, with all Phase 8 safety controls active.
10. **Dependencies on earlier phases**: Phases 2, 8.
11. **Explicitly excluded work**: Binance Production/Live credentials;
    Futures; any change to risk/halt logic.

Tasks:
- 9.1 `ISpotOrderGateway` Binance Testnet implementation (place/cancel/
      query/fills).
- 9.2 `BinanceSpotExecutionAdapter` (maps pipeline commands ↔ gateway).
- 9.3 Reconciliation wiring against real testnet responses + error
      taxonomy (rejections, filter violations, rate limits, timeouts).
- 9.4 End-to-end Testnet integration test suite.

---

## Phase 10 — Binance Spot live trading (gated rollout)

1. **Purpose**: Enable real Binance Spot trading for opted-in users under
   strict entitlement and risk gating, reusing the exact code path proven
   in Phase 9.
2. **Features**: Live-mode enablement flow (explicit user action, platform
   entitlement check, RiskOfficer/Administrator visibility), production
   Key Vault wiring, staged rollout (small user cohort first).
3. **Projects affected**: `Trading.Exchanges.Binance` (Live endpoint
   config), `Trading.Application`, `Trading.Web` (live-trading enablement
   UI with explicit warnings), `deploy/bicep` (Production Key Vault/App
   Service config).
4. **Main entities and interfaces**: Reuses Phase 9 types;
   `TradingModeFlags.LiveTradingEnabled` becomes settable per account.
5. **Database changes**: None structural; `ExchangeAccounts.Environment`
   now includes `Live` rows in Production.
6. **Security considerations**: Production Key Vault access restricted to
   Production Managed Identity only; enabling live trading is an audited,
   explicit, reversible action; default remains disabled for all new
   accounts.
7. **Trading and financial risks**: First phase where real funds move;
   requires the full Phase 8 risk/halt/reconciliation stack to already be
   proven; staged rollout (internal/small cohort first) with close
   monitoring before general availability; no profitability claims
   anywhere in the enablement UI.
8. **Required tests**: All Phase 8/9 tests re-run against the Live
   configuration path (using a minimal real-money smoke test only in a
   tightly controlled manual/staged process, not in ordinary CI);
   entitlement-gating tests (`LiveTradingEnabled` defaults false, requires
   explicit action + eligible plan).
9. **Acceptance criteria**: A small, explicitly opted-in cohort can run
   live Spot trading with all safety controls active, monitored via
   Application Insights alerts, with an operator emergency-stop verified
   in the live environment.
10. **Dependencies on earlier phases**: Phases 8, 9.
11. **Explicitly excluded work**: Futures/leverage; expanding beyond the
    initial rollout cohort until stability is demonstrated operationally.

Tasks:
- 10.1 Production Key Vault + Managed Identity Bicep configuration.
- 10.2 Live-trading enablement use case + audited UI flow with warnings.
- 10.3 Staged rollout cohort gating (entitlement/allow-list check).
- 10.4 Alerting rules for live-trading anomalies in Application Insights.

---

## Phase 11 — Binance Futures testnet trading

1. **Purpose**: Extend execution to Binance USD-M Futures on Testnet as a
   capability separate from Spot, including margin/position tracking.
2. **Features**: `IFuturesOrderGateway`, futures position tracking (margin,
   mark price, liquidation price, funding), reduce-only/close-only support,
   long/short handling.
3. **Projects affected**: `Trading.Exchanges.Binance`, `Trading.Risk`
   (futures-specific limits), `Trading.Application`, `Trading.Workers.
   Execution`, `Trading.Web` (futures position UI).
4. **Main entities and interfaces**: `IFuturesOrderGateway`,
   `BinanceFuturesExecutionAdapter`, extended `Position` fields
   (`MarginMode`, `Leverage`, `LiquidationPrice`, `MarkPrice`,
   `FundingAccrued`).
5. **Database changes**: `Positions` gains futures-specific columns (see
   `docs/database-plan.md` §8); no separate leverage flag added to
   `Orders` (Futures is modeled as a distinct `MarketType`, not a Spot
   order property, per architecture rules).
6. **Security considerations**: Same as Phase 2/9 secret handling, scoped
   to a Futures-capable `ExchangeAccount`.
7. **Trading and financial risks**: Requires current account/margin data
   before increasing exposure; leverage is never auto-increased; no
   martingale or unrestricted averaging-down logic is implemented; stale
   data blocks exposure-increasing orders exactly as in Spot.
8. **Required tests**: Long/short position tracking correctness; margin/
   liquidation/funding calculation tests; leverage-never-auto-increases
   test; stale-data-blocks-increase test on Futures specifically;
   reduce-only/close-only enforcement tests.
9. **Acceptance criteria**: An experiment worker can open, manage, and
   close long/short Futures positions on Binance Testnet with accurate
   margin/liquidation/funding tracking and full risk-engine coverage.
10. **Dependencies on earlier phases**: Phases 8, 9 (reuses the pipeline
    and reconciliation machinery), and Phase 10 must show Spot live is
    stable before Futures Live (Phase 12) — Testnet Futures itself only
    requires Phase 9's proven pipeline pattern.
11. **Explicitly excluded work**: Futures Live trading (Phase 12); cross-
    margin between Spot and Futures (kept as separate capabilities).

Tasks:
- 11.1 `IFuturesOrderGateway` Binance Testnet implementation.
- 11.2 `BinanceFuturesExecutionAdapter`.
- 11.3 Futures position tracking (margin/mark price/liquidation/funding).
- 11.4 Futures-specific risk limits (leverage ceiling, exposure staleness).
- 11.5 Futures position UI (long/short, reduce-only/close-only controls).

---

## Phase 12 — Binance Futures live trading (gated, after Spot live is stable)

1. **Purpose**: Enable real leveraged Futures trading only once Spot live
   trading (Phase 10) has demonstrated operational stability.
2. **Features**: Same gated-enablement pattern as Phase 10, applied to
   Futures, with stricter default risk ceilings (lower default leverage,
   tighter daily loss limits).
3. **Projects affected**: Same as Phase 10, futures-scoped.
4. **Main entities and interfaces**: Reuses Phase 11 types.
5. **Database changes**: None structural.
6. **Security considerations**: Same as Phase 10, plus a distinct
   entitlement flag (`FuturesEligible`) separate from Spot live
   eligibility.
7. **Trading and financial risks**: Highest-risk phase in the plan;
   requires explicit sign-off; default leverage ceilings conservative;
   no auto-leverage-increase, no martingale, no unrestricted averaging
   down (all structurally enforced in Phase 11's risk limits, verified
   again here under live conditions).
8. **Required tests**: Same category as Phase 10's live tests, futures-
   scoped; liquidation-scenario handling tests (exchange-reported
   liquidation reflected correctly in `Position.Status`).
9. **Acceptance criteria**: A small, explicitly opted-in cohort can run
   live Futures trading with all safety controls active and verified.
10. **Dependencies on earlier phases**: Phases 10 (must be stable), 11.
11. **Explicitly excluded work**: Any expansion of leverage ceilings beyond
    the conservative defaults without a separate, explicit review.

Tasks:
- 12.1 Futures live-trading entitlement + enablement UI/audit flow.
- 12.2 Conservative default risk ceilings for leveraged live trading.
- 12.3 Liquidation-event handling + alerting.
- 12.4 Staged rollout + monitoring, mirroring Phase 10.

---

## Phase 13 — User administration, plans, trials, entitlements

1. **Purpose**: Give Administrators tools to manage users, invitations, the
   approved strategy catalog, and plan/trial entitlements gating feature
   access (e.g., number of experiment workers, live/futures eligibility).
2. **Features**: Admin user list/management, strategy-template approval
   workflow, plan definitions, trial expiry handling, entitlement checks
   wired into experiment worker creation and live/futures enablement.
3. **Projects affected**: `Trading.Application`, `Trading.Infrastructure.
   Data`, `Trading.Web` (admin console).
4. **Main entities and interfaces**: `Plan`, `Entitlement`,
   `StrategyTemplate.IsApproved` workflow.
5. **Database changes**: `Plans`, `Entitlements` (see
   `docs/database-plan.md` §10).
6. **Security considerations**: Admin console requires Administrator role
   + MFA; entitlement changes are audited.
7. **Trading and financial risks**: Entitlement checks must not be
   bypassable client-side (server-enforced only); no billing/payment
   processing is implemented yet — this phase only prepares the data
   model so billing can be added later without redesign.
8. **Required tests**: Entitlement enforcement tests (e.g., 11th
   experiment worker rejected for a plan capped at 10 or fewer; live/
   futures eligibility gate tests); trial-expiry behavior tests.
9. **Acceptance criteria**: Administrators can manage users, approve
   strategy templates, and assign plans; entitlements are enforced
   server-side across experiment worker and live-trading enablement
   flows.
10. **Dependencies on earlier phases**: Phases 1, 7 (experiment workers to
    gate), 10/12 (live/futures eligibility flags to gate).
11. **Explicitly excluded work**: Actual payment processing/billing
    integration (data model only, per App Instructions "future billing
    preparation").

Tasks:
- 13.1 `Plan`/`Entitlement` domain + repositories.
- 13.2 Entitlement enforcement in experiment worker creation.
- 13.3 Entitlement enforcement in live/futures enablement flows.
- 13.4 Strategy-template approval workflow (admin UI).
- 13.5 Admin user/invitation management console.

---

## Phase 14 — International reporting

1. **Purpose**: Provide informational, non-advisory transaction reports
   respecting each user's language, time zone, and reporting currency,
   with optional country-specific formats.
2. **Features**: `ReportingProfile` selection, transaction report
   generation from Fills/PortfolioUpdate history, export to Blob Storage,
   optional country-profile plug-ins.
3. **Projects affected**: `Trading.Reporting`, `Trading.Infrastructure.
   Data`, `Trading.Infrastructure` (Blob), `Trading.Web`.
4. **Main entities and interfaces**: `ReportingProfile`,
   `TransactionReport`, country-profile extension point (no country
   hard-coded in Domain/Application).
5. **Database changes**: `ReportingProfiles`, `TransactionReports` (see
   `docs/database-plan.md` §11).
6. **Security considerations**: Reports may contain sensitive financial
   history; access is strictly owner-only, audited on generation/download.
7. **Trading and financial risks**: Every report carries an explicit
   "not tax/legal/financial advice" disclaimer; no currency/format/
   time-zone/country is hard-coded anywhere in Domain/Application.
8. **Required tests**: Report correctness against known Fill/
   PortfolioUpdate fixtures across at least two locales/currencies/time
   zones; disclaimer presence test; owner-only access test.
9. **Acceptance criteria**: A user can select language/time zone/reporting
   currency and generate an accurate, exportable transaction report.
10. **Dependencies on earlier phases**: Phase 7+ (needs Fills/
    PortfolioUpdate history).
11. **Explicitly excluded work**: Actual tax filing/submission, legal
    advice generation.

Tasks:
- 14.1 `ReportingProfile` domain + user settings UI.
- 14.2 Transaction report generation engine.
- 14.3 Blob Storage export + download flow.
- 14.4 At least one optional country-profile format as a pluggable example.

---

## Phase 15 — Hardening: security review, DR, alerting, billing prep

1. **Purpose**: Mature operational readiness before/alongside wider
   rollout: deeper security review, disaster-recovery drills, alerting
   coverage, and groundwork for future billing.
2. **Features**: Scheduled `/security-review`-style audits, DR runbooks and
   drills (market data + trading state restore), alert rule expansion in
   Application Insights, billing-readiness review of the `Plan`/
   `Entitlement` model (still no payment processing implemented).
3. **Projects affected**: Cross-cutting (`deploy/bicep`, `.github/
   workflows`, `Trading.Application` observability hooks).
4. **Main entities and interfaces**: No new domain types; may add
   `IncidentRecord`/runbook documentation only.
5. **Database changes**: None expected beyond any gaps found during
   review.
6. **Security considerations**: This phase is itself a security/ops
   review point; findings feed back into earlier phases via normal
   change process, not silently patched without tests.
7. **Trading and financial risks**: DR drills specifically validate that
   trading state (orders/positions) can be recovered without duplicate
   submissions (reconciliation replay), and that halts survive a
   failover.
8. **Required tests**: DR drill runbook executed against a
   Test/Staging environment with recorded results; alert-firing tests
   (synthetic anomaly triggers expected alert).
9. **Acceptance criteria**: A documented DR drill has been executed
   successfully; alerting covers the key trading-safety scenarios; a
   security review has been performed with findings tracked to
   resolution.
10. **Dependencies on earlier phases**: All previous phases.
11. **Explicitly excluded work**: Implementing actual payment/billing
    processing (remains future work beyond this plan).

Tasks:
- 15.1 DR runbook authoring + first drill execution.
- 15.2 Alerting rule expansion (staleness, halts, reconciliation backlog,
      liquidation events).
- 15.3 Security review pass across all phases with findings tracked.
- 15.4 Billing-readiness review of `Plan`/`Entitlement` (no implementation).

---

## Sequencing summary

```
Phase 0 (foundations)
  -> Phase 1 (identity/invitations)
    -> Phase 2 (exchange accounts/secrets)
      -> Phase 3 (market data/indicators/charts)
        -> Phase 4 (scanner)
        -> Phase 5 (strategies/backtesting)
          -> Phase 6 (optimization)
          -> Phase 7 (paper trading/experiment workers)
            -> Phase 8 (risk/halts/idempotency/reconciliation)
              -> Phase 9 (Spot testnet live-path)
                -> Phase 10 (Spot live, gated)
                  -> Phase 11 (Futures testnet)
                    -> Phase 12 (Futures live, gated — after Phase 10 stable)
-> Phase 13 (admin/plans/entitlements, can start after Phase 7)
-> Phase 14 (reporting, can start after Phase 7)
-> Phase 15 (hardening, continuous but formally after Phase 12)
```

Phases 4, 6, 13, and 14 can proceed in parallel with later phases once
their direct dependency is met, since they do not gate the live-trading
critical path themselves.
