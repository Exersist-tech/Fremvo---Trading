# Testing Plan

## 1. Principles

- All important behavior has automated tests; xUnit is the test framework.
- No automated test ever places a real order against a live exchange —
  Kraken connector tests use recorded Kraken responses or a mocked/faked
  HTTP layer, never Production credentials, and CI has no Live secrets at
  all.
- Financial calculations (fees, slippage, P&L, indicators, risk limits) are
  covered by precise `decimal`-based unit tests, including edge cases
  (zero, minimum notional, tick/step rounding).
- Backtests are deterministic: the same inputs + seed always produce the
  same `BacktestResult`, verified by a golden/snapshot-style test.
- Architecture rules (Domain purity, dependency direction) are enforced by
  automated architecture tests, not just code review.

## 2. Test layers

| Layer | Scope | Tooling |
|---|---|---|
| Domain unit tests | Entities, value objects, state machines (Order/Position), pure invariants | xUnit, FluentAssertions (or similar) |
| Application unit tests | Use cases/orchestration with faked ports (repositories, exchange, secrets) | xUnit + hand-written fakes or a mocking library |
| Indicator tests | Each indicator against known reference values | xUnit, table-driven cases |
| Strategy tests | Deterministic decisions given scripted `MarketEvent` sequences | xUnit |
| Backtesting tests | Reproducibility, fee/slippage application, no look-ahead bias, partial fills | xUnit, fixed seeds, fixed historical fixtures |
| Optimization tests | Train/validation/holdout separation is enforced; holdout touched at most once | xUnit, guard-violation tests expected to throw/reject |
| Risk engine tests | Limit enforcement, halts at each scope, staleness blocking, duplicate-order guard | xUnit |
| Exchange connector tests (Kraken) | Request/response mapping, filter application, error handling | xUnit against recorded/mocked Kraken HTTP responses; Futures may additionally use the Kraken Futures demo environment |
| Integration tests | EF Core against a real (ephemeral) SQL instance (e.g. SQL container/LocalDB), repository correctness, migrations apply cleanly | xUnit + test containers or LocalDB |
| Web/API tests | AuthN/AuthZ enforcement, cross-user isolation, input validation | xUnit + `WebApplicationFactory` |
| Architecture tests | Domain has no forbidden references; dependency direction holds | xUnit + `NetArchTest`/`ArchUnitNET` |
| End-to-end (later) | Full pipeline MarketEvent → AuditEvent in paper mode | xUnit or a lightweight scenario runner, still no live orders |

## 3. Reproducibility & determinism requirements

- Backtests accept an explicit `RandomSeed`; any randomness (e.g. slippage
  jitter) is derived only from that seed, never wall-clock time.
- Historical datasets used in tests are versioned/immutable fixtures
  checked into test data or referenced from a fixed Blob snapshot.
- Given identical `BacktestConfiguration`, two runs must produce bit-for-bit
  identical `BacktestResult` — enforced by a dedicated determinism test.

## 4. No-look-ahead / data-integrity tests

- A strategy must not be able to see a candle before its `CloseTimeUtc` has
  passed in simulated time; tests assert an exception/rejection if a
  strategy or backtest engine attempts to read an unclosed candle.
- Data-quality tests inject missing, duplicate, stale, late, and
  out-of-order candles and assert the ingestion pipeline flags them
  correctly and that signal generation is blocked on affected data.
- Derived-candle tests assert a 10-minute candle is only built from ten
  closed 1-minute candles and is marked `IsDerived`.

## 5. Trading-safety tests

- Order/position state machine tests cover every legal and illegal
  transition, including that `Unknown` can only be resolved by
  `Reconciliation`.
- Idempotency tests assert that resubmitting the same `ClientOrderId` after
  a simulated crash never creates a duplicate order.
- Risk tests assert platform ceilings cannot be overridden by user
  configuration, and that stale data blocks new/increasing orders while
  still allowing reduce-only/close-only actions.
- Halt-switch tests cover all scopes (Global/User/Account/Strategy) and
  confirm a halt at any scope prevents new order submission at/under that
  scope.
- Experiment worker isolation tests confirm a faulted worker does not
  affect the other nine (e.g., simulate an exception in one worker's
  strategy loop and assert others continue).

## 6. Security tests

- Authorization tests assert a user cannot read/modify another user's
  `ExchangeAccount`, `Order`, `Position`, `ExperimentWorker`, or reports.
- Secret-handling tests assert no `SecretReference`/secret value is ever
  serialized into an API response, log entry, or error message
  (e.g., snapshot test of serialized DTOs verifying absence of secret
  fields).
- MFA-required tests assert Administrator-only actions are rejected
  without MFA-verified session claims.

## 7. CI gating

- Every pull request runs: build (warnings as errors where practical),
  unit tests, architecture tests, and fast integration tests.
- Slower integration/E2E suites (real SQL container, Kraken Futures demo calls)
  run on a scheduled/gated job, not necessarily every commit, but must pass
  before any change touching the trading pipeline or connectors merges.
- Live-trading-enabling changes require passing the full suite plus a
  manual sign-off step recorded as part of the PR/deployment process.

## 8. What is explicitly out of scope for automated tests

- No test suite ever runs against Kraken Production/Live credentials.
- No test asserts or predicts strategy profitability; performance metrics
  in tests validate calculation correctness only, not trading outcomes.
