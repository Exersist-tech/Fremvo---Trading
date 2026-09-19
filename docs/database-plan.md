# Database Plan (Azure SQL)

No migrations exist yet; this describes the target schema and its phased
introduction, mapped to `docs/implementation-plan.md`. All monetary/quantity
columns are `decimal(p,s)` (never `float`/`real`). All timestamps are
`datetime2` stored in UTC. Every table has an audited `CreatedAtUtc`;
mutable tables also have `RowVersion` (concurrency token).

## 1. Identity & access (Phase 1)

- `Users` (Id, Email [unique], DisplayName, Locale, TimeZoneId,
  ReportingCurrency, Status, MfaEnabled, CreatedAtUtc, RowVersion)
- `Roles`, `UserRoles` (Id, UserId FK, Role)
- `Invitations` (Id, Code [unique], IssuedByUserId, MaxUses, UsedCount,
  ExpiresAtUtc, Status)
- `AuditEvents` (Id, ActorUserId nullable, Action, TargetType, TargetId,
  CorrelationId, OccurredAtUtc, DetailsJson [no secrets], IPAddressHash
  nullable)

## 2. Exchange accounts & secrets (Phase 2)

- `ExchangeAccounts` (Id, UserId FK, Exchange, Environment
  [Testnet/Live], SupportsSpot bit, SupportsFutures bit, Status,
  SecretReferenceId FK, LastValidatedAtUtc, GrantedPermissionsJson
  [safe metadata only, never secret values])
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

## 5. Strategies (Phase 5)

- `StrategyTemplates` (Id, Code [unique], Name, Description, IsApproved,
  ApprovedAtUtc, ApprovedByUserId)
- `StrategyParameterDefinitions` (Id, StrategyTemplateId FK, Name, Type,
  MinValue, MaxValue, DefaultValue, Step)
- `StrategyParameterSets` (Id, StrategyTemplateId FK, ValuesJson,
  ValidatedAgainstDefinitionsVersion)

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
