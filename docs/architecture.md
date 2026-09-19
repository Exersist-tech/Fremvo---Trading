# Architecture

## 1. Solution structure (planned — not yet created)

A modular monolith: one .NET solution, multiple projects, few deployables.
No project is created until an approved implementation task requests it.

```
Trading.sln
  src/
    Trading.Domain/                 # pure domain, no external deps
    Trading.Application/             # use cases, ports (interfaces), orchestration
    Trading.Infrastructure.Data/     # EF Core, Azure SQL, repositories
    Trading.Infrastructure.Secrets/   # Key Vault-backed secret store
    Trading.Infrastructure.Messaging/ # Azure Service Bus abstraction
    Trading.Infrastructure.Cache/     # Redis abstraction
    Trading.Exchanges.Abstractions/   # exchange-neutral connector contracts
    Trading.Exchanges.Binance/        # Binance-specific connector (Spot + Futures)
    Trading.MarketData/               # ingestion, normalization, candle storage access
    Trading.Indicators/                # technical indicator library (pure functions)
    Trading.Strategies/                # approved strategy templates + parameter contracts
    Trading.Backtesting/               # simulation engine, fee/slippage models
    Trading.Optimization/              # parameter search, train/validation/holdout/walk-forward
    Trading.Risk/                      # risk engine, halts, limits
    Trading.Reporting/                 # international reporting, exports
    Trading.Web/                        # Blazor Web App (UI + minimal APIs)
    Trading.Workers.MarketData/         # Worker Service: ingestion
    Trading.Workers.Scanner/            # Worker Service: market scanner
    Trading.Workers.Experiments/         # Worker Service: up to 10 isolated experiment workers (paper/live)
    Trading.Workers.Execution/           # Worker Service: order execution + reconciliation
  tests/
    Trading.Domain.Tests/
    Trading.Application.Tests/
    Trading.Exchanges.Binance.Tests/
    Trading.Backtesting.Tests/
    Trading.Risk.Tests/
    Trading.Web.Tests/
    ... (one test project per source project with meaningful logic)
  deploy/
    bicep/                              # Azure infrastructure as code
  .github/workflows/                    # CI/CD
```

### Dependency direction (strict)

```
Trading.Domain  <-- depends on nothing platform-specific
      ^
Trading.Application  <-- depends only on Domain (defines ports/interfaces)
      ^
Trading.Infrastructure.*  <-- implement Application ports; depend on Domain+Application
Trading.Exchanges.Binance  <-- implements Trading.Exchanges.Abstractions ports
Trading.MarketData / Strategies / Backtesting / Optimization / Risk / Reporting
      <-- depend on Domain + Application abstractions, never on Infrastructure or Binance directly
Trading.Web / Trading.Workers.*  <-- composition roots; wire concrete implementations via DI
```

`Trading.Domain` must never reference: Azure SDKs, EF Core, ASP.NET Core,
HTTP clients, Binance types, or any UI framework. This is enforced with
architecture tests (e.g. `NetArchTest`/`ArchUnitNET`) in
`Trading.Domain.Tests`.

## 2. Exchange abstraction

`Trading.Exchanges.Abstractions` defines exchange-neutral ports such as:

- `IExchangeConnector` — capability discovery (Spot/Futures supported,
  symbol filters, permissions).
- `IMarketDataSource` — historical + streaming candle/trade retrieval.
- `ISpotOrderGateway`, `IFuturesOrderGateway` — place/cancel/query orders,
  never withdraw.
- `IAccountGateway` — balances, positions, margin info (read-only).
- `ExchangeSymbol`, `PriceTick`, `QuantityStep`, `OrderFilterSet` — neutral
  value objects representing exchange trading rules.

`Trading.Exchanges.Binance` implements these ports using Binance's official
API/SDK, translating Binance-specific DTOs into neutral Domain/Application
models at the boundary. No Binance type ever crosses into Domain,
Strategies, Backtesting, or Risk projects.

Adding a second exchange later means adding `Trading.Exchanges.<Name>` with
the same ports — no change to Domain, Strategies, Backtesting, Risk.

## 3. Trading pipeline (mandatory shape, from App Instructions)

```
MarketEvent -> StrategyDecision -> TradeIntent -> RiskEvaluation
  -> ExecutionCommand -> PaperOrExchangeAdapter -> Reconciliation
  -> PortfolioUpdate -> AuditEvent
```

- Strategies never call exchange connectors directly; they only emit
  `StrategyDecision` objects consumed by the Application layer.
- `RiskEvaluation` is a mandatory gate before any `ExecutionCommand` is
  created; it can reject, downsize, or convert an order to reduce-only, but
  never silently changes an order's intent into a materially different one
  without recording that transformation as an explicit, audited decision.
- `PaperOrExchangeAdapter` is the single seam where paper trading and real
  exchange execution diverge; both implement the same
  `IExecutionAdapter` port so strategy/risk code is identical in both modes.
- `Reconciliation` always runs after execution (and on startup/recovery)
  before allowing further order submission for that
  account/strategy/symbol, so an unknown-status order is never blindly
  retried.
- Every stage emits a correlated event persisted for audit and replay.

## 4. Azure architecture (target)

- **Azure App Service (Linux)** hosts `Trading.Web` (Blazor Web App +
  minimal APIs for any first-party clients).
- **Worker Services** (`Trading.Workers.*`) run as separate App Service
  (Linux) instances or container apps — one deployable per worker kind, so
  the market-data ingester, scanner, experiment workers, and execution
  engine can scale/fail independently. Each of the 10 experiment worker
  "slots" is a logical isolation unit inside `Trading.Workers.Experiments`
  (separate state, not necessarily separate processes at first — see
  `docs/implementation-plan.md` Phase "Experiment workers").
- **Azure SQL Database** is the system of record for users, accounts,
  strategies, orders, positions, audit events, reports.
- **Azure Blob Storage** stores historical market data exports, backtest
  result artifacts, and generated reports (cheaper, high-volume, append-
  mostly data).
- **Redis (Azure Cache for Redis)** holds only ephemeral/real-time state:
  latest prices, scanner intermediate results, rate-limit counters, SignalR
  backplane. Nothing in Redis is a system of record; it can be flushed
  without permanent data loss.
- **Azure Service Bus** carries durable messages that must not be lost:
  market events feeding strategies, trade intents, execution commands,
  reconciliation triggers. Queues/topics per pipeline stage, with dead-letter
  handling and poison-message alerting.
- **Azure Key Vault + Managed Identity** stores exchange API secrets (as
  Key Vault secrets) and connection strings/certificates. SQL stores only a
  Key Vault secret URI/reference and non-secret metadata (label,
  permissions granted, environment, created/rotated timestamps).
- **Application Insights** for logging, distributed tracing (correlation ID
  through the full pipeline above), metrics, and alerting.
- **GitHub Actions** for CI (build/test/lint/architecture tests) and CD
  (Bicep deployment, App Service deployment slots with staged rollout).
- **Bicep** defines all infrastructure; environments are Dev/Test/
  (eventually) Production, each with separate Key Vaults and Binance
  credentials (testnet vs. live never share an environment).

## 5. Environments and trading modes

| Environment | Exchange endpoint | Purpose |
|---|---|---|
| Dev | Binance Testnet | Local/dev iteration |
| Test/Staging | Binance Testnet | Automated + manual QA, paper trading validation |
| Production (paper) | Binance Live market data, simulated execution | Real prices, fake orders |
| Production (live) | Binance Live | Real orders, gated per user/account/strategy |

A single Production deployment supports both paper and live trading modes
per experiment worker/account; live is opt-in and gated by entitlements,
risk checks, and explicit administrator/user enablement — never a separate
deployment that could drift from the tested paper path.

## 6. Cross-cutting concerns

- **Correlation IDs** flow from `MarketEvent` through `AuditEvent` and into
  Application Insights traces.
- **Idempotency keys** (client order IDs) are generated deterministically
  from `(experiment/account, strategy, decision id)` so retries after a
  crash cannot double-submit.
- **Time**: all persisted timestamps are UTC; user-facing display converts
  using the user's selected time zone at the presentation edge only.
- **Configuration**: Azure App Configuration or App Service settings +
  Key Vault references; no secrets in `appsettings.json`.
