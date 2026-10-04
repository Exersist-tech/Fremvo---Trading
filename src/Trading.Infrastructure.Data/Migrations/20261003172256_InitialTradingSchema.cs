using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF scaffolds index-column arrays in the migration body.

namespace Trading.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialTradingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TargetId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Before = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    After = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Candles",
                columns: table => new
                {
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    OpenTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CloseTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Open = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    High = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    Low = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    Close = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    Volume = table.Column<decimal>(type: "decimal(28,12)", nullable: false),
                    IsClosed = table.Column<bool>(type: "bit", nullable: false),
                    IsDerived = table.Column<bool>(type: "bit", nullable: false),
                    QualityFlags = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Candles", x => new { x.Symbol, x.Interval, x.OpenTimeUtc });
                });

            migrationBuilder.CreateTable(
                name: "ExchangeAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeKind = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CredentialReference = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastValidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    StageChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ProvingNotionalCeiling = table.Column<decimal>(type: "decimal(18,8)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExchangeAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentDecisionRecords",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupConfigurationVersion = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<int>(type: "int", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyVersion = table.Column<int>(type: "int", nullable: false),
                    StrategyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    OpenTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CloseTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AsOfUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EvidenceFingerprint = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentDecisionRecords", x => new { x.UserId, x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion, x.StrategyFingerprint, x.Symbol, x.Interval, x.OpenTimeUtc, x.CloseTimeUtc, x.AsOfUtc });
                });

            migrationBuilder.CreateTable(
                name: "ExperimentPaperExecutionAssociations",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupConfigurationVersion = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<int>(type: "int", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyVersion = table.Column<int>(type: "int", nullable: false),
                    StrategyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    OpenTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CloseTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AsOfUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ExecutionCommandId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentPaperExecutionAssociations", x => new { x.UserId, x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion, x.StrategyFingerprint, x.Symbol, x.Interval, x.OpenTimeUtc, x.CloseTimeUtc, x.AsOfUtc });
                });

            migrationBuilder.CreateTable(
                name: "ExperimentPaperPlanEvidence",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupConfigurationVersion = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<int>(type: "int", nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyVersion = table.Column<int>(type: "int", nullable: false),
                    StrategyFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    OpenTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CloseTimeUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AsOfUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ProtectiveStopPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    ConservativeTargetPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentPaperPlanEvidence", x => new { x.UserId, x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion, x.StrategyFingerprint, x.Symbol, x.Interval, x.OpenTimeUtc, x.CloseTimeUtc, x.AsOfUtc });
                });

            migrationBuilder.CreateTable(
                name: "ExperimentResultSnapshots",
                columns: table => new
                {
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SnapshotKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GroupConfigurationVersion = table.Column<int>(type: "int", nullable: false),
                    Group = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    StrategyVersion = table.Column<int>(type: "int", nullable: false),
                    ParametersFingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    DatasetFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ClassifierVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GateEvidenceFingerprint = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Seed = table.Column<int>(type: "int", nullable: false),
                    ReproducibilityIdentity = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    EvaluatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Equity = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    Cash = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    PositionQuantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    RealizedProfitAndLoss = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    UnrealizedProfitAndLoss = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    MaximumDrawdown = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    Fees = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    Slippage = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    RejectedFillCount = table.Column<int>(type: "int", nullable: true),
                    RejectedActionCount = table.Column<int>(type: "int", nullable: true),
                    Exposure = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    GateFailureCount = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentResultSnapshots", x => new { x.OwnerUserId, x.SnapshotKey });
                });

            migrationBuilder.CreateTable(
                name: "ExperimentWorkers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    StrategyId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    MarketSymbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartingCash = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RandomSeed = table.Column<int>(type: "int", nullable: false),
                    StrategyParameters = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    MaxAdditionsPerPosition = table.Column<int>(type: "int", nullable: false),
                    MaxTotalPurchasedQuantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    MaxTotalPurchasedNotional = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    MaxPositionQuantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    MaxPositionNotional = table.Column<decimal>(type: "decimal(28,8)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentWorkers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HistoricalDatasets",
                columns: table => new
                {
                    VersionIdentity = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Interval = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    FromUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ToUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CandleCount = table.Column<int>(type: "int", nullable: false),
                    ContentFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ContainsOnlyClosedCandles = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HistoricalDatasets", x => x.VersionIdentity);
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MaxUses = table.Column<int>(type: "int", nullable: false),
                    UsedCount = table.Column<int>(type: "int", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrderReconciliations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExchangeOrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ObservedStatus = table.Column<int>(type: "int", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ResolutionReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderReconciliations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Side = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    Price = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClientOrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReduceOnly = table.Column<bool>(type: "bit", nullable: false),
                    CloseOnly = table.Column<bool>(type: "bit", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ExchangeAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ExchangeOrderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    FilledQuantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    LastTransitionAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RequiresReconciliation = table.Column<bool>(type: "bit", nullable: false),
                    ReconciliationReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaperTrainingActivations",
                columns: table => new
                {
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    SlotCount = table.Column<int>(type: "int", nullable: false),
                    SlotsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    QualificationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StrategyAssignmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DurableClosedCandleSource = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedResearchGroupsAndGates = table.Column<bool>(type: "bit", nullable: false),
                    WorkerRiskPolicy = table.Column<bool>(type: "bit", nullable: false),
                    PaperFillPolicy = table.Column<bool>(type: "bit", nullable: false),
                    OutputLedger = table.Column<bool>(type: "bit", nullable: false),
                    ProtectiveScheduler = table.Column<bool>(type: "bit", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChangedBy = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaperTrainingActivations", x => x.OwnerUserId);
                });

            migrationBuilder.CreateTable(
                name: "Positions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StrategyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    MarkPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    OpenedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StopLossPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    TakeProfitPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    LastTransitionAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Symbols = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Interval = table.Column<int>(type: "int", nullable: false),
                    Criteria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResultLimit = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanResults",
                columns: table => new
                {
                    ScanRequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScanRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    Score = table.Column<decimal>(type: "decimal(18,12)", nullable: false),
                    MatchedCriteria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceAsOfUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EvaluatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanResults", x => new { x.ScanRequestId, x.ScanRunId, x.Symbol });
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(254)", maxLength: 254, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Locale = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReportingCurrency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    MultiFactorAuthenticationEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentPaperTradingLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    ExecutionPrice = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    Fee = table.Column<decimal>(type: "decimal(28,8)", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Direction = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentPaperTradingLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentPaperTradingLedgerEntries_ExperimentWorkers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "ExperimentWorkers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_Candles_Symbol_Interval_CloseTimeUtc_OpenTimeUtc",
                table: "Candles",
                columns: new[] { "Symbol", "Interval", "CloseTimeUtc", "OpenTimeUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExchangeAccounts_UserId_ExchangeKind",
                table: "ExchangeAccounts",
                columns: new[] { "UserId", "ExchangeKind" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentDecisionRecords_UserId_WorkerId_AsOfUtc",
                table: "ExperimentDecisionRecords",
                columns: new[] { "UserId", "WorkerId", "AsOfUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentPaperExecutionAssociations_UserId_WorkerId_AsOfUtc",
                table: "ExperimentPaperExecutionAssociations",
                columns: new[] { "UserId", "WorkerId", "AsOfUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentPaperPlanEvidence_UserId_WorkerId_AsOfUtc",
                table: "ExperimentPaperPlanEvidence",
                columns: new[] { "UserId", "WorkerId", "AsOfUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentPaperTradingLedgerEntries_UserId_WorkerId_OccurredAtUtc_Id",
                table: "ExperimentPaperTradingLedgerEntries",
                columns: new[] { "UserId", "WorkerId", "OccurredAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentPaperTradingLedgerEntries_WorkerId",
                table: "ExperimentPaperTradingLedgerEntries",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentResultSnapshots_OwnerUserId_EvaluatedAtUtc",
                table: "ExperimentResultSnapshots",
                columns: new[] { "OwnerUserId", "EvaluatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentWorkers_UserId_Id",
                table: "ExperimentWorkers",
                columns: new[] { "UserId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentWorkers_UserId_Status",
                table: "ExperimentWorkers",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalDatasets_Id",
                table: "HistoricalDatasets",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HistoricalDatasets_Symbol_Interval_FromUtc_ToUtc_CreatedAtUtc_VersionIdentity",
                table: "HistoricalDatasets",
                columns: new[] { "Symbol", "Interval", "FromUtc", "ToUtc", "CreatedAtUtc", "VersionIdentity" });

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_Code",
                table: "Invitations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderReconciliations_OrderId",
                table: "OrderReconciliations",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderReconciliations_ResolvedAtUtc",
                table: "OrderReconciliations",
                column: "ResolvedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ClientOrderId",
                table: "Orders",
                column: "ClientOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_UserId_CreatedAtUtc",
                table: "Orders",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_UserId_Mode",
                table: "Orders",
                columns: new[] { "UserId", "Mode" });

            migrationBuilder.CreateIndex(
                name: "IX_Orders_UserId_RequiresReconciliation",
                table: "Orders",
                columns: new[] { "UserId", "RequiresReconciliation" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_UserId_Mode",
                table: "Positions",
                columns: new[] { "UserId", "Mode" });

            migrationBuilder.CreateIndex(
                name: "IX_Positions_UserId_Status",
                table: "Positions",
                columns: new[] { "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanRequests_OwnerId_CreatedAtUtc_Id",
                table: "ScanRequests",
                columns: new[] { "OwnerId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ScanResults_OwnerId_ScanRequestId_ScanRunId_Rank_Score_Symbol",
                table: "ScanResults",
                columns: new[] { "OwnerId", "ScanRequestId", "ScanRunId", "Rank", "Score", "Symbol" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Candles");

            migrationBuilder.DropTable(
                name: "ExchangeAccounts");

            migrationBuilder.DropTable(
                name: "ExperimentDecisionRecords");

            migrationBuilder.DropTable(
                name: "ExperimentPaperExecutionAssociations");

            migrationBuilder.DropTable(
                name: "ExperimentPaperPlanEvidence");

            migrationBuilder.DropTable(
                name: "ExperimentPaperTradingLedgerEntries");

            migrationBuilder.DropTable(
                name: "ExperimentResultSnapshots");

            migrationBuilder.DropTable(
                name: "HistoricalDatasets");

            migrationBuilder.DropTable(
                name: "Invitations");

            migrationBuilder.DropTable(
                name: "OrderReconciliations");

            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "PaperTrainingActivations");

            migrationBuilder.DropTable(
                name: "Positions");

            migrationBuilder.DropTable(
                name: "ScanRequests");

            migrationBuilder.DropTable(
                name: "ScanResults");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "ExperimentWorkers");
        }
    }
}
