# Architecture

## 1. Solution structure

A modular monolith: one .NET solution, multiple projects, few deployables.
This is the logical module map; implementation is phased. See
`docs/implementation-status.md` for current capabilities and gates.

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
    Trading.Exchanges.Kraken/        # Kraken-specific connector (Spot + Futures)
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
    Trading.Exchanges.Kraken.Tests/
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
Trading.Exchanges.Kraken  <-- implements Trading.Exchanges.Abstractions ports
Trading.MarketData / Strategies / Backtesting / Optimization / Risk / Reporting
      <-- depend on Domain + Application abstractions, never on Infrastructure or Kraken directly
Trading.Web / Trading.Workers.*  <-- composition roots; wire concrete implementations via DI
```

`Trading.Domain` must never reference: Azure SDKs, EF Core, ASP.NET Core,
HTTP clients, Kraken types, or any UI framework. This is enforced with
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

`Trading.Exchanges.Kraken` implements these ports using Kraken's official
API/SDK, translating Kraken-specific DTOs into neutral Domain/Application
models at the boundary. No Kraken type ever crosses into Domain,
Strategies, Backtesting, or Risk projects.

The public Kraken Spot order book follows the same boundary: the connector
consumes Kraken WebSocket v2 snapshots and updates, validates the venue CRC32
checksum, and emits neutral `OrderBookSnapshot`/`OrderBookLevel` values through
`IStreamingOrderBookSource`. The authenticated WebSocket route streams that
read-only public data to the Trade view; it never uses exchange credentials or
routes orders.

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
- **Paper Worker Services** run as two separately supervised, singleton
  continuous Linux WebJobs alongside `Trading.Web` on the current App Service:
  public market data/scanner and simulated experiments/protective exits. They
  are separate processes but share the site's managed identity and capacity.
  Scaling and secret isolation require dedicated worker hosting before a
  live-execution rollout. Each of the 10 experiment worker "slots" is a
  logical isolation unit inside `Trading.Workers.Experiments` (separate
  state, not necessarily separate processes — see
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
  (eventually) Production, each with separate Key Vaults and Kraken
  credentials (proving credentials and live credentials never share an
  environment).

## 5. Environments and trading modes

Kraken publishes a demo environment for **Futures**
(`demo-futures.kraken.com`) but has **no public Spot sandbox**. Kraken's own
guidance is to exercise the Spot API with a real account and minimal size,
and its UAT environment is not self-service. The platform therefore cannot
rely on a Spot testnet as the safety gate before live Spot trading, and
substitutes two mechanisms that together give stronger coverage:

1. A **recorded/replayed Spot response harness**, so automated tests
   exercise the full execution and reconciliation path without ever
   reaching Kraken. No automated test may submit a real order.
2. A **minimum-size proving stage** on the real Spot API, described in
   implementation plan Phase 9, constrained by hard platform ceilings.

| Environment | Spot endpoint | Futures endpoint | Purpose |
|---|---|---|---|
| Dev | Public market data only; execution replayed from recorded responses | Kraken Futures demo | Local/dev iteration; no Spot credentials |
| Test/Staging | Public market data; recorded-response execution | Kraken Futures demo | Automated + manual QA, paper trading validation |
| Production (paper) | Kraken live market data, simulated execution | Kraken live market data, simulated execution | Real prices, fake orders |
| Production (proving) | Kraken live Spot, minimum size, close-only after proving | n/a | Phase 9 proving stage, per-account opt-in |
| Production (live) | Kraken live Spot | Kraken Futures live | Real orders, gated per user/account/strategy |

Because the proving stage uses real funds, it is treated as live trading for
every safety purpose: entitlements, risk ceilings, audit, halts, idempotency,
and reconciliation all apply unchanged. It is distinguished only by a
mandatory notional ceiling and a restricted instrument set.

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

## 7. Billing-readiness assessment (no payment processing)

The current `Plan` is an immutable, unique-code entitlement definition with a
zero-to-ten paper-worker ceiling and separate live/futures *eligibility*
flags. An `Entitlement` belongs to one user and one plan; SQL permits one
active assignment at a time, checks a nullable trial expiry at the requested
UTC instant, and keeps revoked history. Repository writes require an active
MFA-enrolled administrator and write immutable audit events. This is a safe
**data foundation**, not a billable subscription. Paper worker enforcement
is now available as an explicit opt-in shared by the web, scanner, and
experiment hosts. When enabled, a missing, revoked or expired assignment has
zero capacity, and the plan caps worker reservations; the experiment host
checks again before each new or position-increasing simulated entry.
Already-reserved slots are not discarded on expiry so open paper positions
remain available to the separate protective-exit and reconciliation paths.
The default remains the legacy ten-worker paper ceiling for active owners
until owners have
been assigned through the verified-MFA admin flow and rollout is approved.
The Spot live route still defaults off and its approved user cohort is not
an entitlement. If explicitly deployed, both promotion and each manual
real-order submission additionally require an effective live-eligible owner
plan; the service rechecks immediately before venue submission. This does
not grant Futures execution or introduce a verified reduce-only live path.
The service also requires the registered Spot execution route for each
submission, even if the account was promoted before an operator disabled
that route. It checks again after persisting the order and before contacting
Kraken, rejecting and auditing an order whose route was withdrawn. The
gateway stays registered for read-only reconciliation after route removal.
If a plan expires while real exposure remains, stop new submissions,
reconcile any unknown order, and manage the position at Kraken under
operator supervision. Do not interpret this policy as proof of live
readiness.
The Accounts page now exposes an administrator-only plan/owner panel; each
create, assignment, or revocation requires a fresh single-use TOTP from
Key Vault. Invitation issuance and revocation use the same rule; the
single-use bearer code is shown only on issuance and never in audit or list
responses. SQL stores only its SHA-256 digest (160-bit generated codes); a
one-way migration converts existing ASCII codes and deactivates non-ASCII
legacy codes. Registration consumes the invitation atomically with the user
record, so a concurrent revocation cannot reactivate a revoked code. The
repository audits plan changes, and suspended owners are denied
new assignments and effective paper capacity even with paper plan enforcement
off. Administrators can suspend/reactivate a non-admin owner with a
single-use TOTP and audited reason; suspension invalidates that owner's
cookie and blocks new paper entries while preserving reserved positions
for separate protective exit/reconciliation processing. Administrators can
grant or remove the RiskOfficer role for active users with a fresh single-use
TOTP and audited reason. The change is conditional on the user's previous role
and status; existing role-bearing sessions are invalidated on the next request.
For an Administrator grant, the candidate must first prove possession of a
separately operator-provisioned Key Vault TOTP from their own signed-in
account. Within five minutes an existing administrator uses a different fresh,
single-use TOTP and an audited reason to approve the exact active user; the
role and MFA-required flag change atomically. The old user cookie is rejected
and the candidate must sign in again with MFA. Neither secret is issued to
the browser or stored in SQL. This flow does not provision the Key Vault
secret or authorize live trading. An existing administrator can revoke another
administrator's role, but not their own or the last active administrator's.
The former administrator's session is rejected; operators must separately
revoke their Key Vault MFA secret.
Paper plan creation always sets live/futures eligibility false.

Before billing can be introduced, the application still needs secure
out-of-band credential delivery, private invitation delivery, Futures enablement
controls, an explicitly verified live close-only route, and operational proof
that expiry preserves protective exits and
reconciliation. The model has no paid period, scheduled future assignment,
grace period, invoice/credit ledger, pricing currency, payment method, refund,
proration, or webhook reconciliation. An expired trial still occupies the
single active assignment slot until an administrator extends it with an
audited, fresh-MFA decision or explicitly revokes it.
These gaps must be designed and tested independently; they must not be
filled by interpreting the presence of an `Entitlement` as permission to
trade. Billing events would require authenticated provider callbacks,
idempotency, an immutable financial ledger, and jurisdiction-specific
handling outside the trading Domain. No payment credentials or processor
payloads belong in SQL audit records or worker telemetry.

## Futures exposure admission (demo prerequisite)

`FuturesExposureRiskEvaluator` is a pure, exchange-neutral prerequisite for
**increasing** linear-contract exposure. It cannot submit an order and is not
registered as a trading route. Callers must provide one current, same-quote
account-wide gross-notional observation, available margin, observed leverage,
an independently timestamped position snapshot, and a current market mark.
Missing, future, non-UTC, or more-than-30-second-old evidence blocks
increases, as do unresolved orders. The evaluated order notional is
`quantity × verified base-asset amount per linear contract × limit price`;
projected gross exposure is the observed gross
notional plus that full order notional (deliberately conservative, not a net
position estimate). Required additional margin is order notional divided by
the **observed** leverage, which may not exceed 2x. Limit-to-mark divergence
may not exceed 2%; both notional ceilings are supplied explicitly in the
same quote currency, never silently converted. The evaluator does not lower
or increase exchange leverage and has no live/futures execution wiring.
An observed futures liquidation must supply the venue's cumulative realized
P&L; neither a prior mark nor an estimated liquidation threshold is used to
fabricate a fill price or realized result.
The separate linear `FuturesPosition` aggregate now also requires that
verified base-asset-per-contract multiplier at creation; contract quantities,
unrealized P&L and reduce-only realized P&L all use it. Kraken's raw
`contractSize` alone cannot supply that value until its units are verified
for the supported instrument.
The isolated demo futures command rejects undefined sides, non-UTC creation
times and a reduce-only instruction whose buy/sell side would increase its
named long/short position. This validation cannot authorize an order by
itself; a future entry still needs current account, position, margin and risk
evidence before any adapter invocation.
The demo adapter rejects every exposure-increasing (`reduceOnly: false`)
command before the venue call until a fresh, owner-scoped account/position/
margin evidence provider and this risk evaluation are wired. The separate
gateway's replay-test route is not an application trading route.
The separate read-only demo instrument reader requests public, unexpired
flexible-futures specifications without a credential. It rejects missing,
duplicate, expired, inverse, non-crypto, malformed and inconsistent
specifications, and bounds the response size. Its `contractSize` is the
venue's raw field: Kraken's public schema does not specify its base-asset
units for every contract, so this reader does **not** supply the verified
multiplier to the risk evaluator or enable Futures execution.
Kraken Futures `/sendorder` documents no `validate` switch: a validation-only
demo request is refused locally without network contact, and actual demo
requests omit the unsupported flag. Spot's validation behavior is different.
The demo gateway now treats HTTP failures, malformed JSON, unknown placement
statuses and incomplete open-order records as indeterminate/unavailable;
only documented placement statuses can confirm acceptance, and only complete
open-order fields can establish an open state. An order absent from the open
list remains unknown, not cancelled. Kraken's read-only `/accounts` endpoint
provides USD margin for the multi-collateral account, while `/openpositions`
provides position sizes; neither response alone proves current same-quote
gross notional, contract-specific sizing or a fresh mark for an increase.
No authenticated Futures observation source or exposure-increase route is
enabled on the basis of those endpoints.

## Live Spot fill accounting

The live Spot order query supplies a cumulative executed quantity but no
execution price. Position accounting therefore also requires complete,
cursor-paginated Kraken trade-history evidence for that exchange order ID.
Missing or contradictory trade IDs, quantities, sides, prices or timestamps
do not produce a position update; the UI warns that the book is stale.
Same-direction observed fills increase position quantity with a decimal
weighted entry price, and reductions use the observed fill price rather than
the requested limit. A sell with no recorded long does not create an
imaginary Spot short. SQL commits each live position change together with
its cumulative order state and immutable audit event; both order and
position writes carry their originally read concurrency revisions.
Opening and resolving a live reconciliation record also commits the record,
order and audit together under the same SQL transaction.
An unknown live order remains frozen when a status query reports new fills:
status-only reconciliation cannot advance its cumulative quantity. The
read-only live sync must first match complete trade-history fills and commit
the position, order and audit atomically. Only a subsequent matching status
query clears the reconciliation record; an absent-order reply conflicting
with an applied fill stays unresolved. Reading the owner's live orders
triggers sync followed by owner-scoped reconciliation. The web host also
retries unresolved live records each minute, grouping them by owner and
isolating failures; it performs no placement or cancellation. An unavailable
answer leaves the order frozen.
New live positions are bound to their exchange account, while legacy live
positions remain explicitly unbound until reconciled; their account must not
be guessed. Manual Spot admission prices recorded same-account positions in
the requested pair against the current reference price before applying the
mandatory exposure and total-position-quantity ceilings. Unbound live
positions, another instrument on that account (without a common-quote
valuation), and unresolved account orders block a new submission. Admission
also checks Kraken's signed, read-only
`OpenOrders` response for the selected account before reservation and again
immediately before submission; any venue open order or unavailable/malformed
answer blocks, including orders placed outside this platform. Admission also
fetches only that owner's selected account's signed `BalanceEx` reading,
requires a UTC reading no older than
30 seconds, and verifies enough unreserved **cash** for a buy or owned base
assets for a sell without treating Kraken margin credit as Spot cash. The
selected pair's observed base-asset total must equal the recorded
same-account long quantity, both before and after reservation; external
holdings or an unobserved sale block instead of being mistaken for zero
exposure. Evidence
that has expired before venue submission rejects the persisted order. Pair
assets must match exactly the Kraken balance's original asset codes; unknown
aliases block rather than guess ownership. The per-order notional ceiling is
checked separately from the cumulative exposure ceiling. This remains a
local position ledger check, not account-wide holdings reconciliation: external
trading after the last open-order check, other assets held externally, and fee
currency not stated by Kraken trade history leave live readiness incomplete.
No verified close-only route is
enabled on the basis of these checks.

New live-order persistence serializes same-account admission using a
transaction-scoped SQL application lock, checks for any nonterminal or
reconciliation-required live order, then records the new order before
committing. A competing request returns a blocked outcome without contacting
Kraken; the lock is released before the network call, so it never holds a SQL
transaction across venue I/O. After reservation, admission repeats the fresh
balance check and compares live-position revision identities before contacting
the venue. Changed evidence rejects the saved order instead of using a stale
risk decision. This prevents two platform requests on the same account from
both holding unresolved reservations, but cannot reserve against orders
placed outside the platform. No schema change is required.

The portfolio's read-only Kraken `BalanceEx` request reports total asset
balances, credit, used credit and spot non-margin trade holds. It displays
Kraken's documented available-for-trading calculation
(`balance + credit - credit_used - hold_trade`) and the reported trade hold.
Only the selected account's current, matching free asset amount is consulted
for live admission; displaying the portfolio cannot authorize a Spot order.
This is not cross-product availability proof.
