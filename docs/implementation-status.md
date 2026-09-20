# Implementation Status

Tracks every task defined in `docs/implementation-plan.md`. All tasks start
as **Not started**. Update this file (status + date + PR/commit reference)
as each task is completed, per the "Working method" in
`.github/copilot-instructions.md` — one task at a time, never batching
multiple tasks' status changes into "done" without a build+test pass.

Status legend: `Not started` | `In progress` | `Blocked` | `Done`

## Phase 0 — Solution foundations
| Task | Status | Notes |
|---|---|---|
| 0.1 Solution + project shells + analyzers | Done | 2026-09-19 — solution skeleton and repo-level build settings created; `dotnet build Trading.sln` succeeded. |
| 0.2 Architecture test project (Domain purity) | Done | 2026-09-19 — architecture guard added to ensure the `Trading.Domain` assembly does not reference forbidden Azure/EF/HTTP/Binance dependencies; `dotnet test Trading.sln` passed. |
| 0.3 GitHub Actions CI workflow | Done | 2026-09-19 — CI workflow added for restore, build, and test validation on pushes and pull requests. |

## Phase 1 — Identity, invitations, roles, audit log
| Task | Status | Notes |
|---|---|---|
| 1.1 Domain entities (User/Invitation/Role/AuditEvent) | Done | 2026-09-19 — domain identity, invitation, and audit entities added with validation and tests; build/test passed. |
| 1.2 EF Core mapping + migration | Done | 2026-09-19 — `TradingDbContext` configured for `Users`, `Invitations`, and `AuditEvents` with safe constraints and a persistence test; `dotnet test Trading.sln` passed after adding EF Core InMemory support for the architecture test project. |
| 1.3 Invitation issuance/redemption use cases | Done | 2026-09-19 — `InvitationService` and request model implemented in the Application layer; validation tests pass. **Corrected 2026-09-19:** the `/api/invitations` endpoint was passing the invitee's email address as the invitation code and was not persisting the invitation. On an invitation-only platform that made the code guessable by anyone who knew the address, and registration could never succeed. The endpoint now generates a 160-bit random code via `InvitationCodeGenerator`, persists the invitation, writes an audit event that never contains the code, and requires an authenticated Administrator. Verified at runtime: an unauthenticated `POST /api/invitations` returns 401. |
| 1.4 Registration/login (Identity) in Blazor Web | Done | 2026-09-19 — registration and basic login endpoints added in the web app, backed by the identity service and persistence layer. **Corrected 2026-09-19:** login validated credentials but issued no session, so no endpoint could identify the caller and nothing could be scoped to a user. Cookie authentication is now configured (HttpOnly, SameSite=Strict, Secure, 8-hour sliding expiry) and login signs the user in with a user-id and role claim; `/api/logout` signs out. API callers receive 401/403 instead of a sign-in redirect. Login now returns the same response for an unknown address and a wrong password so it cannot be used to enumerate registered addresses. `CurrentUser` is the single place a user-scoped endpoint may read the owning user id, and never from the request body or query string. |
| 1.5 MFA enrollment/enforcement for Administrator | Done | 2026-09-19 — administrator MFA guard enforced in `User` with `EnsureAdministratorPrivilegeAllowed()`, plus application policy service and tests covering denied/allowed admin actions; `dotnet test Trading.sln` passed. |
| 1.6 Audit event writer + viewer UI | Done | 2026-09-19 — persisted audit writer and query service added in Application/Infrastructure layers, with tests verifying write and most-recent-first ordering; the web app exposes the audit API and build/test pass. |

## Phase 2 — Exchange account connection (Binance, secrets)
| Task | Status | Notes |
|---|---|---|
| 2.1 Exchange abstraction ports + neutral value objects | Done | 2026-09-19 — `Trading.Exchanges.Abstractions` contains neutral exchange account, kind, and status contracts, with architecture tests verifying status transitions and secret metadata restrictions. |
| 2.2 Key Vault-backed ISecretStore | Done | 2026-09-19 — `Trading.Infrastructure.Secrets` defines the safe secret-reference contract and secret store abstraction for later Key Vault-backed implementation. |
| 2.3 Binance account/permission validation gateway | In progress | 2026-09-19 — neutral API permission validation contract and validation service added; no live Binance calls or withdrawal-capable logic are allowed in this task. |
| 2.4 Connect/validate/disconnect use cases | Done | 2026-09-19 — `ExchangeAccountService` validates read+trade permissions and rejects withdraw-capable or stale credentials; tests pass. |
| 2.5 Blazor UI for exchange accounts | Not started | |

## Phase 3 — Market data ingestion, storage, indicators, charts
| Task | Status | Notes |
|---|---|---|
| 3.1 Symbol/Candle domain + repository ports | In progress | 2026-09-19 — market-data primitives (`MarketSymbol`, `Candle`, `DataQualityIssue`, repository ports) are in place; build/test pass after validation-only additions. |
| 3.2 Binance historical candle fetch + normalization | Not started | |
| 3.3 Binance streaming candle ingestion worker | Not started | |
| 3.4 Data-quality detection | In progress | 2026-09-19 — `CandleQualityEvaluator` exists and validates incomplete/derived/out-of-order/duplicate checks; tests pass. |
| 3.5 Derived 10-minute candle builder | In progress | 2026-09-19 — `DerivedCandleBuilder` produces derived ten-minute candles from ten closed one-minute candles and flags them appropriately; tests pass. |
| 3.6 Core indicator library | Not started | |
| 3.7 Blazor charting UI | Not started | |

## Phase 3B — Market universe and instrument eligibility
See `docs/market-universe.md`. All 50 seed pairs are `Tracked` only; none is live-tradable.
| Task | Status | Notes |
|---|---|---|
| 3B.1 Instrument/InstrumentState/AssetClass domain + exclusions | Done | 2026-09-20 — `Instrument`, `InstrumentState` (10 states), `AssetClass`, `InstrumentExclusionReason`, and `AssetClassifier` added to `Trading.Domain.Universe`. Every instrument is created as `Tracked` only and confers nothing; seed membership does not imply the exchange lists it, so a configured pair the catalogue does not return is suspended and visible rather than silently dropped. Identity is exchange name plus the exchange's own symbol id, never a display name, because short tickers are reused across projects. An unrecognised asset classifies as `Unknown`, which is excluded — the classifier never optimistically assumes a cryptocurrency. Stablecoin, fiat, tokenized-equity, and leveraged-token classes are barred even when every catalogue gate passes; leveraged-token detection requires the stem to be a known asset so a coin whose ticker merely ends in `UP`/`DOWN` is not misclassified. Lifting a suspension returns to `Tracked` only and restores no eligibility. 41 tests added; 182 passing. |
| 3B.2 Eligibility grant model (purpose/timeframe/product/mode) | Done | 2026-09-20 — `EligibilityPurpose`, `EligibilityScope`, `InstrumentEligibilityGrant`, and `InstrumentEligibility` added. Eligibility is keyed on (purpose, interval, product type), so a pair eligible for a 4-hour strategy is provably not eligible for a 1-minute strategy and Spot eligibility confers no Futures eligibility. Grants are strictly additive: a purpose cannot be granted while a prerequisite is missing or expired, which makes granting live trading to a non-paper-eligible instrument impossible. Revocation cascades to every dependent purpose in one operation. Every grant carries its evidence timestamp and expires once that evidence exceeds the configured maximum age — including when only a *prerequisite's* evidence goes stale — so a failure to refresh degrades eligibility instead of preserving it. A live grant is rejected without an explicit approving administrator. 18 tests added; 200 passing. |
| 3B.3 EligibilityGate evaluation engine + explainable record | Done | 2026-09-20 — `EligibilityGate` (12 gates), `EligibilityGateResult`, `EligibilityThresholds`, `EligibilityEvaluation`, and `InstrumentEligibilityEvaluator` added. Every gate fails closed: absent metrics, unloaded filters, an unknown exchange status, an unknown listing age, or stale evidence all evaluate to *not eligible* rather than being assumed acceptable. The liquidity gate requires **both** the rolling and the median measure, so a single 24-hour volume spike with a failing median cannot grant eligibility. `EligibilityThresholds.ConstrainedBy` takes the stricter of operator configuration and the mandatory platform floor for every field, making it impossible to configure a threshold more permissive than the floor. Each evaluation records every gate with its measured value, threshold, and pass/fail detail, and `Explain()` names each failing gate. Determinism is covered by test. |
| 3B.4 InstrumentMetrics rolling/median model + repository | Done | 2026-09-20 — `InstrumentMetrics` added with all-`decimal` values and UTC windows. Carries rolling, median and minimum quote volume, average and worst spread, estimated slippage, trade frequency, data-gap and stale-event counts, and optional depth. Validation rejects an inverted window, metrics computed before the window they describe ends, a worst spread tighter than the average, and negative values. Unmeasured depth is `null` rather than `0`, so "not measured" is never read as "no depth". `Median` is a `decimal` helper proven resistant to a single spike. Repository port deferred to the persistence task. |
| 3B.5 Binance exchange-information catalogue source + neutral mapping | Done | 2026-09-20 � Neutral `InstrumentCatalogueEntry`, `InstrumentCatalogueSnapshot`, and `IInstrumentCatalogueSource` added to `Trading.Exchanges.Abstractions`; the Binance wire DTOs are `internal` to `Trading.Exchanges.Binance`, so Binance field names, casing, status strings, and string-encoded numbers cannot leak into the core. `BinanceExchangeInfoMapper` performs no I/O and holds no credentials (exchangeInfo is public market data), parses every trading rule to `decimal` with the invariant culture so a host locale cannot change a tick size, leaves an unsupplied or zero-encoded rule `null` rather than defaulting it, skips a symbol whose identity is missing rather than guessing, and throws on a malformed document rather than returning an empty catalogue that would look like a mass delisting. `CatalogueSynchronizer` applies a snapshot without granting anything: absence suspends and clears filters only when the snapshot is **complete**, so a truncated response can never suspend the whole universe; incomplete filters clear the loaded flag; a removed instrument is never reinstated. 17 tests added. |
| 3B.6 50-pair seed as versioned configuration (Tracked only) | Done | 2026-09-20 — `MarketUniverseSeed` added with the 50 configured Binance USDT pairs and a version tag (`2026-09-20.spot-usdt-50`) so any eligibility decision can be traced to the seed it came from. Membership grants nothing: every seeded instrument is created `Tracked`, with no catalogue observation, no grant, and `BlocksNewExposure` true, all covered by test. The live exchange catalogue — not this list — remains the source of truth for existence, trading status, and Spot permission. The seed classifier recognises the seed base assets as cryptocurrencies while keeping the standard stablecoin, fiat, and leveraged-token exclusions; short tickers `G`, `ONE`, and `AR` are explicitly tested as genuine assets rather than leveraged tokens. Several seeded pairs are recent listings expected to fail the listing-age and liquidity gates and to stay research-only; that is the intended outcome, not a defect. 13 tests added; 242 passing. |
| 3B.7 Newly-listed restricted state + evidence-age expiry | Done | 2026-09-20 � `NewListingPolicy` and `NewListingRestriction` added. A new listing has thin, unrepresentative history, so the restriction is expressed in permitted purposes rather than as a warning: tracking only, then research, then simulation, then unrestricted. An unknown listing age is `Unknown` and permits nothing � it is never treated as mature. `Stricter(floor)` takes the longer window for every stage, so configuration can only delay access, never hasten it, and the simulation window provably never permits any test or live purpose. Evidence-age expiry is carried by the grants from 3B.2; `InstrumentEligibility.HasGrant` was added so revocation can distinguish "there was something to withdraw" from "already absent". 9 tests added. |
| 3B.8 Degradation rules (block entries/increases, allow reduction) | Done | 2026-09-20 � `InstrumentDegradationPolicy`, `ExposureDirective`, and `ExposureDecision` added. Conditions are evaluated in strict severity order and every unknown fails closed: absent measurements, stale measurements, and degraded data health all drop to `ReduceOnly`. Delisting, absence from the catalogue, a non-TRADING status, and missing trading rules force `CloseOnly` � without filters an order cannot be correctly sized, so a partial reduction could be rounded into a materially different order and only a full close is safe. `PermitsReduction` is true under **every** directive and is covered by test: blocking exits would trap capital in a deteriorating market. Every decision carries its reason. 13 tests added. |
| 3B.9 Recalculation worker (scheduled + event-triggered) | Done | 2026-09-20 � `UniverseRecalculationService` plus `UniverseRecalculationWorker` hosted in `Trading.Workers.Scanner`. Recalculation is a safety control, not a convenience: it is the mechanism by which an instrument that stops meeting its gates loses its grant without anyone having to notice. It grants at most `Paper` � test and live purposes require an explicit audited approval and are never produced automatically � but it **revokes** them automatically when evidence fails, because withdrawing capability must never need an approval. An evidence failure revokes rather than retains, since not knowing must never read as still qualifying, and one failing instrument never stops the others. Time comes from an injected `TimeProvider`, so passes are deterministic under test. The host binds unconfigured evidence and work sources that report nothing rather than fabricating a universe. 9 tests added. |
| 3B.10 Administrator instrument/eligibility UI with gate explanations | Done | 2026-09-20 � `UniverseAdminQueryService` plus `GET /api/universe/instruments` and the `/admin/universe` page, both restricted to Administrator and RiskOfficer. Every row states *why*: the per-gate measured value, threshold, and detail, the exposure directive with its reason, and the listing-age restriction. An operator is never left to guess why an instrument is unavailable, because guessing invites overriding the gate instead of fixing the evidence. The view model is asserted by test to expose no secret, API-key, user, or account field, and the page renders all values via `textContent` rather than `innerHTML`. The page states that eligibility is never a prediction or a recommendation. Runtime-verified: `/admin/universe` 200, `/api/universe/instruments` 401 unauthenticated. 3 tests added. |

## Phase 4 — Automatic market scanner
| Task | Status | Notes |
|---|---|---|
| 4.1 ScanCriterion/ScanRequest/ScanResult domain | Not started | |
| 4.2 Criteria evaluation engine | Not started | |
| 4.3 Scanner worker scheduling | Not started | |
| 4.4 Blazor scanner results UI | Not started | |

## Phase 5 — Approved strategy templates + backtesting engine
| Task | Status | Notes |
|---|---|---|
| 5.1 IStrategy/StrategyState/parameter-definition domain | In progress | 2026-09-19 — `StrategyParameterDefinition` and `StrategyParameterSet` provide range validation and runtime parameter assignment without exposing live trading or execution code. |
| 5.2 First approved strategy template | In progress | 2026-09-19 — platform-authored momentum breakout template remains in place and validated; additional strategy guardrails are being refined. |
| 5.3 Second approved strategy template | In progress | 2026-09-19 — `MeanReversionStrategyTemplate` added as the second approved template and covered by architecture tests; no live or exchange execution is introduced. |
| 5.4 HistoricalDataset storage/versioning | In progress | 2026-09-19 — `HistoricalDataset` model added with immutability and range validation; no database migration or live execution is introduced. |
| 5.5 Backtest engine core loop | In progress | 2026-09-19 — `BacktestConfiguration` and `BacktestResult` models added for deterministic configuration and result capture; no strategy execution is implemented yet. |
| 5.6 Fee/slippage/filter models | Not started | |
| 5.7 Backtest result reporting UI | Not started | |

## Phase 5B — Ten approved research strategy families
See `docs/strategy-research-plan.md`. These are falsifiable research templates, not strategies expected to be profitable. All start as `Draft`.
| Task | Status | Notes |
|---|---|---|
| 5B.1 Strategy approval state machine + immutable versioned approvals | Not started | |
| 5B.2 Strategy approval requirements (instrument/history/liquidity/spread/slippage/timeframes/product/modes) | Not started | |
| 5B.3 Regime/signal/execution timeframe separation | Not started | |
| 5B.4 Rejection-gate engine with recorded per-gate results | Not started | |
| 5B.5 Family 1 — multi-timeframe EMA trend continuation | Not started | |
| 5B.6 Family 2 — Donchian breakout ensemble | Not started | |
| 5B.7 Family 3 — Bollinger mean reversion (ranging-regime filter) | Not started | |
| 5B.8 Family 4 — RSI pullback within a higher-timeframe trend | Not started | |
| 5B.9 Family 5 — MACD and volume-confirmed trend acceleration | Not started | |
| 5B.10 Family 6 — volatility-compression breakout | Not started | |
| 5B.11 Family 7 — cross-sectional momentum rotation (survivorship-aware) | Not started | |
| 5B.12 Family 8 — relative-strength pullback rotation | Not started | |
| 5B.13 SessionProfile + IANA/DST-safe session engine | Not started | |
| 5B.14 Family 9 — session-conditioned breakout vs no-session baseline | Not started | |
| 5B.15 Deterministic, versioned regime classifier | Not started | |
| 5B.16 Family 10 — regime-switching ensemble | Not started | |
| 5B.17 Derived 4-day candle interval (documented UTC boundary), optional | Not started | |
| 5B.18 Experiment groups A/B/C wiring for the ten workers | Not started | |

## Phase 6 — Optimization (train/validation/holdout/walk-forward)
| Task | Status | Notes |
|---|---|---|
| 6.1 DatasetSplit domain (non-overlap/time-order) | In progress | 2026-09-19 — `DatasetSplit` model added with time-order/non-overlap validation and strict holdout-lock semantics to prevent future-data leakage; tests added and build passes. Fixed a leakage-detection defect where `IsTimeOrderedRelativeTo` returned true for overlapping same-symbol splits; replaced with `IsTimeOrderedAfter` plus a regression test. |
| 6.2 Parameter search algorithm | In progress | 2026-09-19 — `ParameterSearchEngine` added to enumerate candidate ranges and score parameter sets over bounded definitions; tests added and build passes. |
| 6.3 Holdout one-time-verification guard | Done | 2026-09-19 — `HoldoutVerificationGuard` prevents overlap with holdout windows and enforces a single verification event. `OptimizationRun` orchestrator added: splits are validated for time order and non-overlap up front, selection scores are produced on validation data only, holdout scoring is structurally impossible before selection is final, and the holdout is scored exactly once. Tests cover single-holdout-evaluation, re-run rejection, out-of-order splits, overlap, and mixed symbols. |
| 6.4 Walk-forward evaluation + reporting | In progress | 2026-09-19 — `WalkForwardFold` and `WalkForwardEvaluationResult` added to enforce time-ordered, non-overlapping validation windows and summarize results without live execution; tests added and build passes. |
| 6.5 Blazor optimization UI | In progress | 2026-09-19 — `/optimization` page and `POST /api/optimization/validate-plan` endpoint added for split-plan configuration and validation, backed by `OptimizationPlanValidator`. Results rendering is intentionally **not** implemented: scoring requires the backtest engine (task 5.5), and the UI/API explicitly report `canBeExecuted=false` with a blocked reason rather than showing placeholder or simulated scores. Every response carries the no-guarantee disclaimer. Verified at runtime; tests added. |

## Phase 7 — Paper trading + isolated experiment workers
| Task | Status | Notes |
|---|---|---|
| 7.1 Pipeline entities + repositories | Done | 2026-09-19 — Pipeline entities completed with the previously missing `PortfolioUpdate`, plus `PipelineContext`/`PipelineRecord<T>` envelopes and user-scoped repository ports (`IMarketEventRepository` … `IPortfolioUpdateRepository`). Every persisted record carries owning user, trading mode, and correlation id. Repositories expose no unscoped read; a record owned by another user reads as *not found* rather than *forbidden*. In-memory implementations added for paper/experiment use (explicitly non-durable). Isolation, correlation-scoping, and duplicate-id tests added. |
| 7.2 PaperExecutionAdapter | Done | 2026-09-19 — `PaperExecutionAdapter` is now driven through the real pipeline rather than in isolation. `TradePipeline` orchestrator added in `Trading.Application`, enforcing the mandatory sequence MarketEvent → StrategyDecision → TradeIntent → RiskEvaluation → ExecutionCommand → adapter → reconciliation → PortfolioUpdate → AuditEvent. Safety behaviour covered by tests: live trading blocked by default, unclosed candles never generate signals, data-quality flags block the trade, stale account data blocks new exposure, risk denial stops before execution, unknown exchange status demands reconciliation and is never retried, and client order ids are deterministic per intent. |
| 7.3 ExperimentWorker lifecycle + isolation | Done | 2026-09-19 — Hardened for isolation and reproducibility. `RandomSeed` is now a caller-supplied `int` fixed at construction instead of a fresh `Guid` per worker, so a run is reproducible and no two workers share a stream. `Fail(reason)` now retains the reason in `FailureReason` (previously discarded). `ApplyPaperTrade` now requires the worker to be `Running`, requires a positive quantity with the side expressed by direction, refuses to borrow cash, and tracks `RealizedProfitAndLoss` against the average entry price. Repository ports are now user-scoped (`GetAsync(userId, workerId)`, `ListAsync(userId)`, `CountAsync(userId)`): a worker owned by another user reads as *not found*. `ExperimentWorkerPool` contains a faulting worker so the other nine keep running, and records only the exception type so a fault cannot leak credentials. |
| 7.4 Experiments worker host (up to 10) | Done | 2026-09-19 — The host is now a real service instead of a placeholder logging loop. `Worker` advances each enabled user's pool on a configurable interval using an injected `TimeProvider`; a fault in one user's pool never stops another's. `PaperExperimentWorkerRunner` drives each worker through the full `TradePipeline` in Paper mode with the worker's own balance and position as the portfolio snapshot, then applies the resulting fill to that worker's ledger. The composition root registers only the paper execution adapter, so the host structurally cannot reach a live exchange. Market-data ingestion and executable strategy templates are not yet connected to this host: the registered stand-ins resolve nothing and report no closed candle, which leaves workers idle rather than fabricating candles or results. Audit events in this host use the explicitly non-durable in-memory writer; a durable writer is required before this host is used for anything but paper experiments. |
| 7.5 Experiment dashboard UI | Done | 2026-09-19 — `/experiments` lists the signed-in user's workers with per-worker status, seed, cash, position, realized profit and loss, and trade count, and shows how many of the ten slots are used. `GET`/`POST /api/experiments` require authentication and take the owning user from the signed-in principal only; there is no user-id parameter, so one user cannot address another user's workers. The page is labelled paper-only and states that simulated results do not indicate future results and that no strategy is guaranteed to be profitable. Verified at runtime: `/api/experiments` returns 401 when unauthenticated. This task required cookie authentication to exist first, which is recorded as a correction against task 1.4. |

## Phase 8 — Risk engine, halts, idempotency, reconciliation
| Task | Status | Notes |
|---|---|---|
| 8.1 RiskLimit/RiskLimitHierarchy + platform ceilings | In progress | 2026-09-19 — `RiskLimitHierarchy` enforces mandatory platform ceilings and clamps user-level values below those ceilings; tests pass. |
| 8.2 HaltSwitch (all scopes) + emergency stop | Done | 2026-09-19 — halts are now **enforced**, not merely represented. `TradePipeline` previously passed a hard-coded `false` for every halt, duplicate, and conflict flag, so no switch could actually stop a trade. `ITradingHaltState` is now read for each trade and supplies the platform emergency stop, market halt, user/account halt, strategy halt, close-only, and reduce-only states to the risk gate. Tests prove each scope blocks: the emergency stop blocks and releasing it restores trading, a market halt blocks only that symbol, a user halt blocks only that user, and a strategy halt blocks only that strategy. |
| 8.3 TradingModeFlags | Done | 2026-09-19 — close-only and reduce-only are enforced in the pipeline against the actual direction of the trade. Close-only blocks opening a position but allows closing one; reduce-only blocks an exposure increase but allows a reduction. |
| 8.4 StalenessPolicy enforcement | In progress | 2026-09-19 — `StalenessPolicy` is now correctly evaluated. Fixed a defect where supplying any policy with `RequiresFreshData=true` blocked *every* order regardless of actual data age; `RiskEngine.Evaluate` now takes `lastDataUpdateUtc`/`nowUtc` and calls `StalenessPolicy.IsStale`. A missing timestamp still fails safe (treated as stale). A close-only test that had been passing for the wrong reason (short-circuiting on the staleness branch) was strengthened to assert the actual reason, and fresh/expired data cases were added. Enforced in `TradePipeline`. |
| 8.5 DuplicateOrderGuard + idempotent ClientOrderId | Done | 2026-09-19 — the guard is now wired into `TradePipeline` and evaluated as part of the risk gate, using the deterministic client order id derived from the trade intent. Fixed a defect where a client-order-id **conflict** (same id, different payload) was returned with `IsDuplicate = false`, which a caller could not distinguish from acceptance; `OrderIdempotencyResult` now exposes `IsConflict` and `IsAccepted`, and a conflict is never submitted. |
| 8.6 Order/Position state machines | Done | 2026-09-20 — state machines completed and made durable. `Order` now records `ExchangeOrderId`, cumulative `FilledQuantity`, `LastTransitionAtUtc` and a reconciliation freeze; fills are cumulative, cannot decrease and cannot exceed the ordered quantity; terminal orders cannot be submitted again. `Position` gained `RestrictToReduceOnly`, `BeginClosing` and `PermitsIncrease`, so close-only and reduce-only are enforced states rather than labels, and a closed or liquidated position cannot be restricted back into life. Both aggregates carry a `Version` concurrency token. Added `Orders`, `Positions` and `OrderReconciliations` to `TradingDbContext` with `decimal(28,8)` money columns, UTC timestamps, optimistic concurrency, and a **unique index on `ClientOrderId`** so duplicate-order protection survives a restart instead of living only in the in-memory guard. Repository ports scope every read by user id; the single unscoped read exists only for the reconciliation worker and is documented as such. |
| 8.7 Reconciliation service | Done | 2026-09-20 — `OrderReconciliationService` now closes the loop that `TradePipeline` previously only reported. Added the `IExchangeOrderStatusQuery` port in `Trading.Exchanges.Abstractions`, keyed on **client order id** rather than the exchange id, because the exchange id is exactly what is missing when a submission times out. `OrderStatusQueryResult` keeps `NotFound` (the exchange proved no such order) strictly separate from `Unavailable` (nothing was proven); a connector exception degrades to `Unavailable`. Corrected `RequiresResolutionBeforeResubmission`, which previously allowed resubmission of an order the exchange reported as `New` or `Filled`: resubmission now requires positive proof via `ProvesOrderIsNotLive`. A failed query leaves both the record and the order frozen. Every open, resolution and failed query writes an audit event. `TradePipeline` now persists an unresolved reconciliation record when it detects an unknown outcome, so the freeze is durable rather than a return value a caller could ignore. |
| 8.8 Application shell and operator UI | Done | 2026-09-20 — added a shared navigation shell (`wwwroot/app.css`, `wwwroot/nav.js`) mounted on every page, which renders entirely through `textContent` so no server or exchange string can become markup, and which displays a persistent "live trading disabled" badge and a footer stating that the platform never holds or withdraws funds and never predicts results. New `/orders` view shows orders, open positions and outstanding reconciliations, with frozen orders flagged; new `/account` view covers invitation-only registration, sign in and sign out without ever echoing a credential. `/api/orders` and `/api/orders/reconciliations` take the owning user from the signed-in principal only and expose no user id parameter. Verified at runtime: all pages 200 anonymous, both APIs 401 anonymous. |
| 8.8 Admin/risk dashboard UI | Done | 2026-09-19 — `/admin/risk` exposes the safety controls so an operator can actually use them: platform emergency stop, market halt, user halt, strategy halt, close-only, and reduce-only, each with engage and release. `GET`/`POST /api/risk/halts` require the Administrator or RiskOfficer role. Every change requires a reason and writes an immutable audit event naming the actor, the scope, and the target; the page states that a halt does not close existing positions. Verified at runtime: both endpoints return 401 when unauthenticated. Halt state is a singleton, so an emergency stop applies immediately to every trading path in the process. |

## Phase 9 — Binance Spot testnet live-path trading
| Task | Status | Notes |
|---|---|---|
| 9.1 ISpotOrderGateway Binance Testnet implementation | Not started | |
| 9.2 BinanceSpotExecutionAdapter | Not started | |
| 9.3 Reconciliation wiring + error taxonomy | Not started | |
| 9.4 End-to-end Testnet integration test suite | Not started | |

## Phase 10 — Binance Spot live trading (gated rollout)
| Task | Status | Notes |
|---|---|---|
| 10.1 Production Key Vault + Managed Identity (Bicep) | Not started | |
| 10.2 Live-trading enablement use case + audited UI | Not started | |
| 10.3 Staged rollout cohort gating | Not started | |
| 10.4 Alerting rules for live-trading anomalies | Not started | |

## Phase 11 — Binance Futures testnet trading
| Task | Status | Notes |
|---|---|---|
| 11.1 IFuturesOrderGateway Binance Testnet implementation | Not started | |
| 11.2 BinanceFuturesExecutionAdapter | Not started | |
| 11.3 Futures position tracking | Not started | |
| 11.4 Futures-specific risk limits | Not started | |
| 11.5 Futures position UI | Not started | |

## Phase 12 — Binance Futures live trading (gated, after Spot live is stable)
| Task | Status | Notes |
|---|---|---|
| 12.1 Futures live-trading entitlement + enablement flow | Not started | |
| 12.2 Conservative default risk ceilings | Not started | |
| 12.3 Liquidation-event handling + alerting | Not started | |
| 12.4 Staged rollout + monitoring | Not started | |

## Phase 13 — User administration, plans, trials, entitlements
| Task | Status | Notes |
|---|---|---|
| 13.1 Plan/Entitlement domain + repositories | Not started | |
| 13.2 Entitlement enforcement in experiment worker creation | Not started | |
| 13.3 Entitlement enforcement in live/futures enablement | Not started | |
| 13.4 Strategy-template approval workflow | Not started | |
| 13.5 Admin user/invitation management console | Not started | |

## Phase 14 — International reporting
| Task | Status | Notes |
|---|---|---|
| 14.1 ReportingProfile domain + settings UI | Not started | |
| 14.2 Transaction report generation engine | Not started | |
| 14.3 Blob Storage export + download flow | Not started | |
| 14.4 Optional country-profile example | Not started | |

## Phase 15 — Hardening: security review, DR, alerting, billing prep
| Task | Status | Notes |
|---|---|---|
| 15.1 DR runbook authoring + first drill | Not started | |
| 15.2 Alerting rule expansion | Not started | |
| 15.3 Security review pass | Not started | |
| 15.4 Billing-readiness review | Not started | |
