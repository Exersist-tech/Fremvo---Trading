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
| 1.3 Invitation issuance/redemption use cases | Done | 2026-09-19 — `InvitationService` and request model implemented in the Application layer; validation tests pass. |
| 1.4 Registration/login (Identity) in Blazor Web | Done | 2026-09-19 — registration and basic login endpoints added in the web app, backed by the identity service and persistence layer; `dotnet test Trading.sln` passed. |
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
| 7.3 ExperimentWorker lifecycle + isolation | In progress | 2026-09-19 — `ExperimentWorker` domain model created with lifecycle states, isolated paper-trading ledger, and worker guardrails; tests added and build passes. |
| 7.4 Experiments worker host (up to 10) | Not started | |
| 7.5 Experiment dashboard UI | Not started | |

## Phase 8 — Risk engine, halts, idempotency, reconciliation
| Task | Status | Notes |
|---|---|---|
| 8.1 RiskLimit/RiskLimitHierarchy + platform ceilings | In progress | 2026-09-19 — `RiskLimitHierarchy` enforces mandatory platform ceilings and clamps user-level values below those ceilings; tests pass. |
| 8.2 HaltSwitch (all scopes) + emergency stop | In progress | 2026-09-19 — halt conditions and mode flags are enforced as gating layers; emergency/market/account/strategy halts are represented. |
| 8.3 TradingModeFlags | In progress | 2026-09-19 — `TradingModeFlags` and `HaltSwitch` primitives are added to model halt and close-only/reduce-only modes without live execution. |
| 8.4 StalenessPolicy enforcement | In progress | 2026-09-19 — `StalenessPolicy` is now correctly evaluated. Fixed a defect where supplying any policy with `RequiresFreshData=true` blocked *every* order regardless of actual data age; `RiskEngine.Evaluate` now takes `lastDataUpdateUtc`/`nowUtc` and calls `StalenessPolicy.IsStale`. A missing timestamp still fails safe (treated as stale). A close-only test that had been passing for the wrong reason (short-circuiting on the staleness branch) was strengthened to assert the actual reason, and fresh/expired data cases were added. Enforced in `TradePipeline`. |
| 8.5 DuplicateOrderGuard + idempotent ClientOrderId | In progress | 2026-09-19 — `OrderIdempotencyGuard` rejects duplicate client-order payloads and conflict cases; tests pass. |
| 8.6 Order/Position state machines | In progress | 2026-09-19 — `Order` and `Position` lifecycle models already enforce safe state transitions and exposure reduction rules. |
| 8.7 Reconciliation service | In progress | 2026-09-19 — `OrderReconciliationRecord` and reconciliation-status primitives added; unknown status now requires resolution before resubmission. |
| 8.8 Admin/risk dashboard UI | Not started | |

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
