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
| 0.1 Solution + project shells + analyzers | Not started | |
| 0.2 Architecture test project (Domain purity) | Not started | |
| 0.3 GitHub Actions CI workflow | Not started | |

## Phase 1 — Identity, invitations, roles, audit log
| Task | Status | Notes |
|---|---|---|
| 1.1 Domain entities (User/Invitation/Role/AuditEvent) | Not started | |
| 1.2 EF Core mapping + migration | Not started | |
| 1.3 Invitation issuance/redemption use cases | Not started | |
| 1.4 Registration/login (Identity) in Blazor Web | Not started | |
| 1.5 MFA enrollment/enforcement for Administrator | Not started | |
| 1.6 Audit event writer + viewer UI | Not started | |

## Phase 2 — Exchange account connection (Binance, secrets)
| Task | Status | Notes |
|---|---|---|
| 2.1 Exchange abstraction ports + neutral value objects | Not started | |
| 2.2 Key Vault-backed ISecretStore | Not started | |
| 2.3 Binance account/permission validation gateway | Not started | |
| 2.4 Connect/validate/disconnect use cases | Not started | |
| 2.5 Blazor UI for exchange accounts | Not started | |

## Phase 3 — Market data ingestion, storage, indicators, charts
| Task | Status | Notes |
|---|---|---|
| 3.1 Symbol/Candle domain + repository ports | Not started | |
| 3.2 Binance historical candle fetch + normalization | Not started | |
| 3.3 Binance streaming candle ingestion worker | Not started | |
| 3.4 Data-quality detection | Not started | |
| 3.5 Derived 10-minute candle builder | Not started | |
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
| 5.1 IStrategy/StrategyState/parameter-definition domain | Not started | |
| 5.2 First approved strategy template | Not started | |
| 5.3 Second approved strategy template | Not started | |
| 5.4 HistoricalDataset storage/versioning | Not started | |
| 5.5 Backtest engine core loop | Not started | |
| 5.6 Fee/slippage/filter models | Not started | |
| 5.7 Backtest result reporting UI | Not started | |

## Phase 6 — Optimization (train/validation/holdout/walk-forward)
| Task | Status | Notes |
|---|---|---|
| 6.1 DatasetSplit domain (non-overlap/time-order) | Not started | |
| 6.2 Parameter search algorithm | Not started | |
| 6.3 Holdout one-time-verification guard | Not started | |
| 6.4 Walk-forward evaluation + reporting | Not started | |
| 6.5 Blazor optimization UI | Not started | |

## Phase 7 — Paper trading + isolated experiment workers
| Task | Status | Notes |
|---|---|---|
| 7.1 Pipeline entities + repositories | Not started | |
| 7.2 PaperExecutionAdapter | Not started | |
| 7.3 ExperimentWorker lifecycle + isolation | Not started | |
| 7.4 Experiments worker host (up to 10) | Not started | |
| 7.5 Experiment dashboard UI | Not started | |

## Phase 8 — Risk engine, halts, idempotency, reconciliation
| Task | Status | Notes |
|---|---|---|
| 8.1 RiskLimit/RiskLimitHierarchy + platform ceilings | Not started | |
| 8.2 HaltSwitch (all scopes) + emergency stop | Not started | |
| 8.3 TradingModeFlags | Not started | |
| 8.4 StalenessPolicy enforcement | Not started | |
| 8.5 DuplicateOrderGuard + idempotent ClientOrderId | Not started | |
| 8.6 Order/Position state machines | Not started | |
| 8.7 Reconciliation service | Not started | |
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
