# Database Plan (Azure SQL)

This is the original phased target; EF migrations now exist under
`src/Trading.Infrastructure.Data/Migrations`, and the actual model and
migrations take precedence over this early plan. All monetary/quantity
columns are `decimal(p,s)` (never `float`/`real`). All timestamps are
`datetime2` stored in UTC. Every table has an audited `CreatedAtUtc`;
mutable tables also have `RowVersion` (concurrency token).

## 1. Identity & access (Phase 1)

- `Users` (Id, Email [unique], DisplayName, Locale, TimeZoneId,
  ReportingCurrency, Status, MfaEnabled, CreatedAtUtc, RowVersion)
- `Roles`, `UserRoles` (Id, UserId FK, Role)
- `Invitations` (Id, Code [unique SHA-256 digest of a random bearer code],
  RecipientEmail, IssuedByUserId, MaxUses, UsedCount, ExpiresAtUtc, IsActive).
  The raw code is returned only at issuance and cannot be recovered from SQL.
- `AuditEvents` (Id, ActorUserId nullable, Action, TargetType, TargetId,
  CorrelationId, OccurredAtUtc, DetailsJson [no secrets], IPAddressHash
  nullable)

## 2. Exchange accounts & secrets (Phase 2)

- `ExchangeAccounts` (Id, UserId FK, ExchangeKind, DisplayName,
  CredentialReference, CreatedAtUtc, LastValidatedAtUtc, Status,
  **Stage [Paper/Proving/Live], StageChangedAtUtc**)
  - `Stage` is an integer where `0` is `Paper`. The safe value is the default
    value on purpose: a row written without an explicit stage falls back to the
    one that cannot reach the exchange.
  - `CredentialReference` holds the secret's **name**, never a secret value.
    The name is scoped per user (`exchange-credential/{userId}/{accountId}`) so
    two users' credentials can never collide, and a database compromise alone
    does not yield a tradable key.
  - Disconnecting or suspending an account resets `Stage` to `Paper`, so
    re-connecting a key never silently restores a previous live clearance.
- `SecretReferences` (Id, KeyVaultName, KeyVaultSecretName,
  KeyVaultSecretVersion, CreatedAtUtc, RotatedAtUtc nullable) — **no secret
  value column exists in SQL at all.**

## 3. Market data (Phase 3)

- `Symbols` (Id, Exchange, BaseAsset, QuoteAsset, PriceTick decimal,
  QuantityStep decimal, MinNotional decimal, MinQuantity decimal,
  MaxQuantity decimal, IsActive bit)
- `Candles` (Id, SymbolId FK, Interval, OpenTimeUtc, CloseTimeUtc,
  Open/High/Low/Close decimal(28,10), Volume decimal(28,10), IsDerived bit,
  IsClosed bit, SourceSequence bigint nullable, DataQualityFlags int
  [bitmask]) — unique index on (SymbolId, Interval, OpenTimeUtc);
  clustered/partitioned by time for large volume; considered for migration
  to Blob-backed cold storage after a retention window, with Azure SQL
  holding recent/hot data.

## 4. Market scanner (Phase 4)

- `ScanRequests` (Id, Name, CriteriaJson, ScheduleCron, IsEnabled)
- `ScanResults` (Id, ScanRequestId FK, EvaluatedAtUtc, SymbolId FK, Rank,
  MatchedCriteriaJson)

## 4A. Market universe & instrument eligibility (Phase 3B)

See `docs/market-universe.md`.

- `Instruments` (Id, Exchange, ExchangeSymbol [unique per exchange],
  BaseAsset, QuoteAsset, AssetClass int, LifecycleState int,
  ExchangeStatus nvarchar, PermissionsJson, FiltersLoadedAtUtc,
  FirstObservedCandleUtc, ExchangeOnboardUtc null, IsConfiguredSeed bit,
  IsPresentOnExchange bit, LastCatalogueSyncUtc)
- `InstrumentEligibilityGrants` (Id, InstrumentId FK, Purpose int,
  Interval int, ProductType int, TradingMode int, IsGranted bit,
  GrantedAtUtc, EvidenceAsOfUtc, ApprovedByUserId null) — unique index on
  (InstrumentId, Purpose, Interval, ProductType, TradingMode). Live grants
  require a non-null `ApprovedByUserId`.
- `InstrumentMetrics` (Id, InstrumentId FK, WindowStartUtc, WindowEndUtc,
  RollingQuoteVolume decimal(28,10), MedianQuoteVolume decimal(28,10),
  MinQuoteVolume decimal(28,10), AverageSpread decimal(28,10),
  WorstSpread decimal(28,10), EstimatedSlippageJson, DepthJson null,
  TradeFrequency decimal(28,10), DataGapCount int, DataGapSeconds bigint,
  StaleEventCount int, ListingAgeDays int, ComputedAtUtc)
- `InstrumentEligibilityEvaluations` (Id, InstrumentId FK, EvaluatedAtUtc,
  Purpose int, Interval int, ProductType int, TradingMode int, Passed bit,
  GateResultsJson [gate, measured value, threshold, pass/fail])
- `InstrumentStateTransitions` (Id, InstrumentId FK, FromState int,
  ToState int, TriggeringGate int null, Reason nvarchar, OccurredAtUtc,
  ActorUserId null) — append-only.

Seed data (the 50 USD research pairs) is versioned configuration, not a
migration constant, and inserts every pair as `Tracked` with **no**
eligibility grant rows.

## 5. Strategies (Phase 5)

- `StrategyTemplates` (Id, Code [unique], Name, Description, IsApproved,
  ApprovedAtUtc, ApprovedByUserId)
- `StrategyParameterDefinitions` (Id, StrategyTemplateId FK, Name, Type,
  MinValue, MaxValue, DefaultValue, Step)
- `StrategyParameterSets` (Id, StrategyTemplateId FK, ValuesJson,
  ValidatedAgainstDefinitionsVersion)

## 5A. Strategy research approvals (Phase 5B)

See `docs/strategy-research-plan.md`,
`docs/strategy-validation-matrix.md`, and
`docs/strategy-approval-workflow.md`. These are future schema requirements;
the design task adds no migration.

- `StrategyVersions` (Id, StrategyTemplateId FK, SemanticVersion,
  ContentFingerprint, ParameterSchemaFingerprint,
  ParameterDefinitionsJson, RegimeInterval int, SignalInterval int,
  ExecutionInterval int null, WarmUpCandles int, CreatedAtUtc) — immutable;
  unique on (StrategyTemplateId, SemanticVersion) and on the content
  fingerprint within a template.
- `StrategyApprovals` (Id, StrategyVersionId FK, ApprovalState int,
  SupportedInstrumentClassJson, MinimumHistoryCandles int,
  MinimumLiquidity decimal(28,10), MaximumSpread decimal(28,10),
  MaximumEstimatedSlippage decimal(28,10), SupportedIntervalsJson,
  ProductType int, ApprovedTradingModesJson, ApprovedByUserId null,
  ApprovedAtUtc null) — immutable; a change creates a new row. All
  strategies start in `Draft` with no approver. The future migration uses the
  explicit stable mapping `Draft=0`, `ResearchApproved=1`,
  `BacktestApproved=2`, `ForwardPaperAuthorized=3`, `PaperApproved=4`,
  `Suspended=5`, `Rejected=6`, `Retired=7`; live/Futures approval values do
  not exist in this workflow. Existing legacy rows require an explicit
  reviewed migration and must not be reinterpreted by ordinal.
- `StrategyApprovalEvidence` (Id, StrategyVersionId FK, EvidenceFingerprint,
  SourceCommit, BuildIdentity, DatasetFingerprint, UniverseFingerprint,
  CostModelFingerprint, TimeZoneProfileFingerprint null, RandomSeed,
  PartitionBoundariesJson, AttemptedParametersJson, ResultsJson,
  CreatedAtUtc) — immutable; contains no secrets.
- `StrategyApprovalTransitions` (Id, StrategyVersionId FK, FromState,
  ToState, StrategyApprovalEvidenceId FK null, ActorUserId, Reason,
  CorrelationId, OccurredAtUtc) — append-only. Automated service identities
  may attach evidence but cannot approve.
- `SessionProfiles` (Id, Name, IanaTimeZone, LocalStartTime, LocalEndTime,
  AllowedWeekdays int [bitmask], DaylightSavingPolicy int,
  MinimumLiquidity decimal(28,10), MaximumSpread decimal(28,10),
  StrategyMode int, TimeZoneDatabaseVersion nvarchar, VersionNumber)
- `RegimeEvaluations` (Id, InstrumentId FK, ClassifierVersion,
  EvaluatedAtUtc, RegimeState int, InputsJson) — inputs are recorded so
  the decision is reproducible and provably timestamp-bounded.
- `RejectionGateResults` (Id, BacktestRunId FK, Gate int, Passed bit,
  MeasuredValue decimal(28,10) null, Threshold decimal(28,10) null,
  Detail nvarchar)
- `HoldoutEvaluations` (Id, StrategyVersionId FK, DatasetId FK,
  EvaluatedAtUtc, ResultJson) — unique on (StrategyVersionId, DatasetId)
  so untouched holdout data can be evaluated exactly once.
- `StrategyGateResults` (Id, StrategyApprovalEvidenceId FK, GateCode,
  Passed, MeasuredValuesJson, FrozenThresholdsJson, Detail) — immutable;
  unique on (StrategyApprovalEvidenceId, GateCode).

## 6. Backtesting & optimization (Phases 5–6)

- `HistoricalDatasets` (Id, SymbolId FK, Interval, StartUtc, EndUtc,
  VersionTag, IsImmutable bit)
- `DatasetSplits` (Id, HistoricalDatasetId FK, SplitType
  [Training/Validation/Holdout/WalkForwardFold], FoldIndex nullable,
  StartUtc, EndUtc)
- `BacktestRuns` (Id, StrategyParameterSetId FK, DatasetSplitId FK,
  FeeModelJson, SlippageModelJson, StartingBalance decimal, RandomSeed,
  StartedAtUtc, CompletedAtUtc, ResultSummaryJson)
- `BacktestTrades` (Id, BacktestRunId FK, SymbolId FK, Side, Quantity
  decimal, Price decimal, Fee decimal, OccurredAtUtc)
- `OptimizationRuns` (Id, StrategyTemplateId FK, ParameterSpaceJson,
  ObjectiveMetric, BestParameterSetId FK nullable, HoldoutVerifiedAtUtc
  nullable) — application logic guarantees Holdout is touched at most once
  per `OptimizationRun`, enforced additionally by this single nullable
  timestamp column (set-once).

## 7. Experiment workers (Phase 7)

### 7.1 Planned continuous scanner records

These tables replace activation-time preassignment only after a separately
approved implementation and migration. Existing worker, decision, claim, fill,
position and ledger rows remain authoritative and are never rewritten.

The initial single-host implementation stores bounded scan observations,
queue disposition, and admitted slots in the existing optimistic-concurrency
`PaperTrainingActivations` row. This preserves existing development data and
provides restart-safe idempotency without a destructive schema rebuild. The
following normalized tables remain the required scale-out migration before
multiple scanner instances are deployed:

- `StrategyScanRuns` (Id, OwnerUserId, StartedAtUtc, CompletedAtUtc null,
  UniverseFingerprint, RankingPolicyVersion, Status, EligiblePairCount,
  ObservationCount, Detail) — one bounded five-minute scan.
- `StrategyCandidateObservations` (Id, ScanRunId FK, OwnerUserId,
  StrategyVersionId FK, InstrumentId FK, TimeframeProfileVersion,
  SignalOpenTimeUtc, SignalCloseTimeUtc, UniverseFingerprint,
  EvidenceFingerprint, BullishVotes, BearishVotes, Decision, VetoesJson,
  ExpiresAtUtc, CreatedAtUtc) — immutable; unique on owner, strategy version,
  instrument, profile, signal candle and universe fingerprint.
- `RankedPaperOpportunities` (Id, CandidateObservationId FK, OwnerUserId,
  RankingPolicyVersion, Score decimal(28,10), FactorsJson, Rank,
  State, ExpiresAtUtc, SupersededById null, RejectionReason null,
  RowVersion) — scanner/queue state only; it allocates no worker.
- `PaperOpportunityAdmissions` (Id, RankedOpportunityId FK unique,
  OwnerUserId, ExperimentWorkerId FK unique, CapacitySequence,
  AdmittedAtUtc, ReleasedAtUtc null, State, RowVersion) — admission and worker
  creation are atomic. Application transaction/concurrency checks enforce at
  most ten unreleased admissions per owner.

Queue rows are not workers. A worker row is inserted only with an admitted
opportunity. Unknown execution status keeps admission capacity reserved until
reconciliation; a fully closed/reconciled worker terminates and releases the
admission instead of being reassigned.

- `PaperTrainingActivations` (OwnerUserId PK, State, SlotCount,
  SlotsJson, QualificationsJson, prerequisite flags, ChangedAtUtc, ChangedBy,
  RowVersion) — one owner-scoped durable activation. `SlotsJson` contains only
  server-approved strategy/symbol slot identities and fake starting balances.
  `QualificationsJson` records per-slot historical return, completed trades,
  maximum drawdown, dataset SHA-256 fingerprint, acceptance, and a safe reason.
  The rowversion protects concurrent Start/Stop transitions. No credential,
  secret reference, API payload, or live-order capability is stored.
  Existing deployments apply
  `docs/database/20260921-paper-training-qualification.sql` before starting the
  new web and experiment-worker versions; development rebuilds its disposable
  local schema automatically.
- `ExperimentWorkers` (Id, OwnerUserId nullable [platform-owned if null],
  Mode [Paper/Live], StrategyParameterSetId FK, Status, RandomSeed,
  CreatedAtUtc) — constrained to at most 10 concurrently `Running` rows
  per owning scope via application logic + a filtered unique/check
  constraint pattern.
- `ExperimentLedgers` (Id, ExperimentWorkerId FK, Asset, Balance decimal,
  AsOfUtc)
- `ExperimentStrategyStates` (Id, ExperimentWorkerId FK, StateJson,
  UpdatedAtUtc)

## 8. Trading pipeline & orders (Phases 7–10)

- `MarketEventsProcessed` (Id, ExperimentWorkerId FK, SymbolId FK,
  CandleId FK, CorrelationId, ProcessedAtUtc) — dedupe/audit trail, not a
  full event store (event store detail may live in Service Bus + Blob
  archive rather than hot SQL).
- `StrategyDecisions` (Id, ExperimentWorkerId FK, CorrelationId, DecisionType,
  RationaleJson, OccurredAtUtc)
- `TradeIntents` (Id, StrategyDecisionId FK, Symbol, Side, RequestedQuantity
  decimal, RequestedPrice decimal nullable, OrderType, ReduceOnly bit)
- `RiskEvaluations` (Id, TradeIntentId FK, Outcome
  [Approved/Rejected/Modified], Reason, ModifiedIntentJson nullable,
  EvaluatedAtUtc)
- `ExecutionCommands` (Id, RiskEvaluationId FK, ClientOrderId [unique],
  CommandType, IssuedAtUtc)
- `Orders` (Id, ExecutionCommandId FK, ExchangeAccountId FK nullable
  [null for pure paper], ClientOrderId [unique], ExchangeOrderId nullable,
  Symbol, MarketType [Spot/Futures], Side, OrderType, Quantity decimal,
  Price decimal nullable, ReduceOnly bit, Status, CreatedAtUtc,
  LastUpdatedAtUtc, RowVersion)
- `Fills` (Id, OrderId FK, Quantity decimal, Price decimal, Fee decimal,
  FeeAsset, OccurredAtUtc)
- `ReconciliationRecords` (Id, OrderId FK, ExpectedStatus, ActualStatus,
  Reconciled bit, ReconciledAtUtc, Notes)
- `Positions` (Id, ExchangeAccountId FK nullable, ExperimentWorkerId FK
  nullable, Symbol, MarketType, Side [Futures], Quantity decimal,
  AverageEntryPrice decimal, UnrealizedPnl decimal, RealizedPnl decimal,
  MarginMode nullable, Leverage nullable, LiquidationPrice nullable,
  MarkPrice nullable, Status, LastUpdatedAtUtc, RowVersion)
- `PortfolioUpdates` (Id, OrderId FK nullable, PositionId FK nullable,
  BalanceDeltaJson, OccurredAtUtc)

## 9. Risk engine (Phase 8)

- `RiskLimits` (Id, Scope [Platform/User/Account/Strategy], ScopeId
  nullable, MaxPositionSize decimal nullable, MaxLeverage decimal nullable,
  MaxDailyLoss decimal nullable, MaxOrderRatePerMinute int nullable,
  MaxOpenPositions int nullable, IsPlatformCeiling bit)
- `HaltSwitches` (Id, Scope, ScopeId nullable, IsHalted bit, Reason,
  SetByUserId, SetAtUtc)
- `TradingModeFlags` (Id, Scope, ScopeId nullable, CloseOnly bit,
  ReduceOnly bit, LiveTradingEnabled bit default 0, LeverageTradingEnabled
  bit default 0)
- `DuplicateOrderGuards` (ClientOrderId PK, FirstSeenAtUtc,
  ExpiresAtUtc)

## 10. Plans, trials, entitlements (Phase 13)

- `Plans` (Id, Code, Name, MaxExperimentWorkers, LiveTradingEligible bit,
  FuturesEligible bit)
- `Entitlements` (Id, UserId FK, PlanId FK, TrialExpiresAtUtc nullable,
  Status)

## 11. Reporting (Phase 14)

- `ReportingProfiles` (Id, UserId FK, Language, TimeZoneId,
  ReportingCurrency, CountryProfileCode nullable)
- `TransactionReports` (Id, UserId FK, PeriodStartUtc, PeriodEndUtc,
  Format, BlobStorageUri, GeneratedAtUtc)

The initial **paper-only** JSON export uses the existing immutable owner-scoped
`AuditEvents` rows as an export index (report ID, UTC interval, reporting
currency, size and SHA-256 content hash), avoiding a new table and schema
adoption for this bounded feature. The `TransactionReports` table above
remains a future design for broader report types and lifecycle policies;
never store report payloads or a public Blob URL in audit records.

## 12. General conventions

- Every FK is indexed; every hot lookup path (Candles by symbol+interval+
  time, Orders by ClientOrderId, ExecutionCommands by ClientOrderId) has a
  supporting unique or covering index.
- Soft-delete is not used for financial/audit tables; status enums track
  lifecycle instead, preserving history.
- Migrations are additive-first (expand/contract pattern) to support
  zero-downtime deploys: add nullable columns/new tables in one release,
  backfill, then tighten constraints in a later release.
- No migration or seed script ever inserts a real/live exchange secret;
  test fixtures use fake `SecretReference` rows pointing at fake Key Vault
  entries in non-production Key Vaults.
