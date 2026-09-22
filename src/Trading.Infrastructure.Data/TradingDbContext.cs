using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Domain.Users;
using Trading.Infrastructure.Data.Backtesting;
using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Data.MarketData;
using Trading.Infrastructure.Data.Scanner;
using Trading.Infrastructure.Data.Experiments;

namespace Trading.Infrastructure.Data;

public sealed class TradingDbContext : DbContext
{
    public TradingDbContext(DbContextOptions<TradingDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<ExchangeAccount> ExchangeAccounts => Set<ExchangeAccount>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<OrderReconciliationRecord> OrderReconciliations => Set<OrderReconciliationRecord>();

    public DbSet<PersistedCandle> Candles => Set<PersistedCandle>();

    public DbSet<PersistedScanRequest> ScanRequests => Set<PersistedScanRequest>();

    public DbSet<PersistedScanResult> ScanResults => Set<PersistedScanResult>();

    public DbSet<PersistedHistoricalDataset> HistoricalDatasets => Set<PersistedHistoricalDataset>();
    public DbSet<PersistedExperimentDecisionRecord> ExperimentDecisionRecords => Set<PersistedExperimentDecisionRecord>();
    public DbSet<PersistedExperimentPaperExecutionAssociation> ExperimentPaperExecutionAssociations => Set<PersistedExperimentPaperExecutionAssociation>();
    public DbSet<PersistedExperimentPaperPlanEvidence> ExperimentPaperPlanEvidence => Set<PersistedExperimentPaperPlanEvidence>();
    public DbSet<PersistedExperimentResultSnapshot> ExperimentResultSnapshots => Set<PersistedExperimentResultSnapshot>();
    public DbSet<PersistedPaperTrainingActivation> PaperTrainingActivations => Set<PersistedPaperTrainingActivation>();
    public DbSet<PersistedExperimentWorker> ExperimentWorkers => Set<PersistedExperimentWorker>();
    public DbSet<PersistedPaperTradingLedgerEntry> PaperTradingLedgerEntries => Set<PersistedPaperTradingLedgerEntry>();

    /// <summary>
    /// Precision used for every monetary and quantity column.
    /// </summary>
    /// <remarks>
    /// Eight decimal places matches the smallest unit quoted by the supported
    /// exchanges. Mapping these as SQL <c>decimal</c> rather than a floating
    /// point type is mandatory: a rounding error in a quantity becomes a
    /// rejected or materially different order.
    /// </remarks>
    internal const string MoneyColumnType = "decimal(28,8)";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);

            entity.Property(user => user.Email)
                .HasMaxLength(254)
                .IsRequired();

            entity.HasIndex(user => user.Email)
                .IsUnique();

            entity.Property(user => user.DisplayName)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(user => user.Locale)
                .HasMaxLength(16)
                .IsRequired();

            entity.Property(user => user.TimeZone)
                .HasMaxLength(64)
                .IsRequired();

            entity.Property(user => user.ReportingCurrency)
                .HasMaxLength(3)
                .IsRequired();

            // The password verifier, not the password. It is a one-way hash
            // with its own salt and cost parameters embedded, so the stored
            // value cannot be reversed and is useless if the database leaks
            // without also brute-forcing each password individually.
            //
            // Nullable by design: an invited user has no password until they
            // set one, and that account must not be able to sign in.
            entity.Property(user => user.PasswordHash)
                .HasMaxLength(256);

            entity.Property(user => user.Role)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(user => user.Status)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(user => user.MultiFactorAuthenticationEnabled)
                .IsRequired();
        });

        modelBuilder.Entity<Invitation>(entity =>
        {
            entity.ToTable("Invitations");
            entity.HasKey(invitation => invitation.Id);

            entity.Property(invitation => invitation.Code)
                .HasMaxLength(64)
                .IsRequired();

            entity.HasIndex(invitation => invitation.Code)
                .IsUnique();

            entity.Property(invitation => invitation.IssuedByUserId)
                .IsRequired();

            entity.Property(invitation => invitation.MaxUses)
                .IsRequired();

            entity.Property(invitation => invitation.UsedCount)
                .IsRequired();

            entity.Property(invitation => invitation.ExpiresAtUtc)
                .IsRequired();

            entity.Property(invitation => invitation.IsActive)
                .IsRequired();
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("AuditEvents");
            entity.HasKey(auditEvent => auditEvent.Id);

            entity.Property(auditEvent => auditEvent.ActorUserId)
                .IsRequired(false);

            entity.Property(auditEvent => auditEvent.Action)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(auditEvent => auditEvent.TargetType)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(auditEvent => auditEvent.TargetId)
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(auditEvent => auditEvent.OccurredAtUtc)
                .IsRequired();

            entity.Property(auditEvent => auditEvent.CorrelationId)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(auditEvent => auditEvent.Before)
                .HasColumnType("nvarchar(max)");

            entity.Property(auditEvent => auditEvent.After)
                .HasColumnType("nvarchar(max)");

            entity.HasIndex(auditEvent => auditEvent.CorrelationId);
        });

        modelBuilder.Entity<PersistedPaperTrainingActivation>(entity =>
        {
            entity.ToTable("PaperTrainingActivations");
            entity.HasKey(value => value.OwnerUserId);
            entity.Property(value => value.State).IsRequired();
            entity.Property(value => value.SlotCount).IsRequired();
            entity.Property(value => value.SlotsJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(value => value.QualificationsJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(value => value.ChangedAtUtc).IsRequired();
            entity.Property(value => value.ChangedBy).IsRequired();
            entity.Property(value => value.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<PersistedExperimentWorker>(entity =>
        {
            entity.ToTable("ExperimentWorkers");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Name).HasMaxLength(200).IsRequired();
            entity.Property(value => value.StrategyId).HasMaxLength(128).IsRequired();
            entity.Property(value => value.MarketSymbol).HasMaxLength(64).IsRequired();
            entity.Property(value => value.StrategyParameters).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(value => value.FailureReason).HasMaxLength(512);
            entity.Property(value => value.StartingCash).HasColumnType(MoneyColumnType);
            entity.Property(value => value.MaxTotalPurchasedQuantity).HasColumnType(MoneyColumnType);
            entity.Property(value => value.MaxTotalPurchasedNotional).HasColumnType(MoneyColumnType);
            entity.Property(value => value.MaxPositionQuantity).HasColumnType(MoneyColumnType);
            entity.Property(value => value.MaxPositionNotional).HasColumnType(MoneyColumnType);
            entity.HasIndex(value => new { value.UserId, value.Id }).IsUnique();
            entity.HasIndex(value => new { value.UserId, value.Status });
            entity.HasMany(value => value.LedgerEntries).WithOne().HasForeignKey(value => value.WorkerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PersistedPaperTradingLedgerEntry>(entity =>
        {
            entity.ToTable("ExperimentPaperTradingLedgerEntries");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Symbol).HasMaxLength(64).IsRequired();
            entity.Property(value => value.Direction).HasMaxLength(8).IsRequired();
            entity.Property(value => value.Quantity).HasColumnType(MoneyColumnType);
            entity.Property(value => value.ExecutionPrice).HasColumnType(MoneyColumnType);
            entity.Property(value => value.Fee).HasColumnType(MoneyColumnType);
            entity.HasIndex(value => new { value.UserId, value.WorkerId, value.OccurredAtUtc, value.Id });
        });

        modelBuilder.Entity<ExchangeAccount>(entity =>
        {
            entity.ToTable("ExchangeAccounts");
            entity.HasKey(account => account.Id);

            entity.Property(account => account.UserId)
                .IsRequired();

            entity.Property(account => account.ExchangeKind)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(account => account.DisplayName)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(account => account.CredentialReference)
                .HasMaxLength(256)
                .IsRequired();

            entity.Property(account => account.CreatedAtUtc)
                .IsRequired();

            entity.Property(account => account.LastValidatedAtUtc)
                .IsRequired(false);

            entity.Property(account => account.Status)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(account => account.Stage)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(account => account.StageChangedAtUtc)
                .IsRequired(false);

            // Money, so decimal with an explicit precision. Nullable because
            // "no ceiling set" is a distinct state from "a ceiling of zero",
            // and the risk engine blocks on the former.
            entity.Property(account => account.ProvingNotionalCeiling)
                .HasColumnType("decimal(18,8)")
                .IsRequired(false);

            entity.HasIndex(account => new { account.UserId, account.ExchangeKind })
                .IsUnique(false);
        });

        modelBuilder.Entity<PersistedExperimentDecisionRecord>(entity =>
        {
            entity.ToTable("ExperimentDecisionRecords");
            entity.HasKey(x => new { x.UserId, x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion,
                x.StrategyFingerprint, x.Symbol, x.Interval, x.OpenTimeUtc, x.CloseTimeUtc, x.AsOfUtc });
            entity.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.StrategyFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(512).IsRequired();
            entity.Property(x => x.EvidenceFingerprint).HasMaxLength(1024).IsRequired();
            entity.HasIndex(x => new { x.UserId, x.WorkerId, x.AsOfUtc });
        });

        modelBuilder.Entity<PersistedExperimentPaperExecutionAssociation>(entity =>
        {
            entity.ToTable("ExperimentPaperExecutionAssociations");
            entity.HasKey(x => new { x.UserId, x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion,
                x.StrategyFingerprint, x.Symbol, x.Interval, x.OpenTimeUtc, x.CloseTimeUtc, x.AsOfUtc });
            entity.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.StrategyFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Detail).HasMaxLength(1024).IsRequired(false);
            entity.HasIndex(x => new { x.UserId, x.WorkerId, x.AsOfUtc });
        });

        modelBuilder.Entity<PersistedExperimentPaperPlanEvidence>(entity =>
        {
            entity.ToTable("ExperimentPaperPlanEvidence");
            entity.HasKey(x => new { x.UserId, x.WorkerId, x.GroupConfigurationVersion, x.Group, x.StrategyId, x.StrategyVersion,
                x.StrategyFingerprint, x.Symbol, x.Interval, x.OpenTimeUtc, x.CloseTimeUtc, x.AsOfUtc });
            entity.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.StrategyFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ProtectiveStopPrice).HasColumnType(MoneyColumnType);
            entity.Property(x => x.ConservativeTargetPrice).HasColumnType(MoneyColumnType);
            entity.HasIndex(x => new { x.UserId, x.WorkerId, x.AsOfUtc });
        });

        modelBuilder.Entity<PersistedExperimentResultSnapshot>(entity =>
        {
            entity.ToTable("ExperimentResultSnapshots");
            entity.HasKey(x => new { x.OwnerUserId, x.SnapshotKey });
            entity.Property(x => x.SnapshotKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Group).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StrategyId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ParametersFingerprint).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DatasetFingerprint).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClassifierVersion).HasMaxLength(128).IsRequired();
            entity.Property(x => x.GateEvidenceFingerprint).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.ReproducibilityIdentity).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Equity).HasColumnType(MoneyColumnType);
            entity.Property(x => x.Cash).HasColumnType(MoneyColumnType);
            entity.Property(x => x.PositionQuantity).HasColumnType(MoneyColumnType);
            entity.Property(x => x.RealizedProfitAndLoss).HasColumnType(MoneyColumnType);
            entity.Property(x => x.UnrealizedProfitAndLoss).HasColumnType(MoneyColumnType);
            entity.Property(x => x.MaximumDrawdown).HasColumnType(MoneyColumnType);
            entity.Property(x => x.Fees).HasColumnType(MoneyColumnType);
            entity.Property(x => x.Slippage).HasColumnType(MoneyColumnType);
            entity.Property(x => x.Exposure).HasColumnType(MoneyColumnType);
            entity.HasIndex(x => new { x.OwnerUserId, x.EvaluatedAtUtc });
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);

            entity.Property(order => order.UserId).IsRequired();
            entity.Property(order => order.StrategyId).IsRequired();
            // Stored as text so the paper and live books stay distinguishable
            // in the database itself, not only in application code.
            entity.Property(order => order.Mode).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(order => order.ExchangeAccountId).IsRequired(false);
            entity.HasIndex(order => new { order.UserId, order.Mode });

            entity.Property(order => order.Symbol)
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(order => order.Side).HasConversion<int>().IsRequired();
            entity.Property(order => order.Type).HasConversion<int>().IsRequired();
            entity.Property(order => order.State).HasConversion<int>().IsRequired();

            entity.Property(order => order.Quantity).HasColumnType(MoneyColumnType).IsRequired();
            entity.Property(order => order.Price).HasColumnType(MoneyColumnType).IsRequired();
            entity.Property(order => order.FilledQuantity).HasColumnType(MoneyColumnType).IsRequired();

            entity.Property(order => order.CreatedAtUtc).IsRequired();
            entity.Property(order => order.LastTransitionAtUtc).IsRequired(false);

            entity.Property(order => order.ClientOrderId)
                .HasMaxLength(Order.MaximumClientOrderIdLength)
                .IsRequired();

            // Durable duplicate-order protection. An in-memory idempotency
            // guard is lost on restart or when a second instance starts, so
            // the database must be the authority on client order id reuse.
            entity.HasIndex(order => order.ClientOrderId).IsUnique();

            entity.Property(order => order.ExchangeOrderId)
                .HasMaxLength(64)
                .IsRequired(false);

            entity.Property(order => order.ReduceOnly).IsRequired();
            entity.Property(order => order.CloseOnly).IsRequired();
            entity.Property(order => order.RequiresReconciliation).IsRequired();

            entity.Property(order => order.ReconciliationReason)
                .HasMaxLength(512)
                .IsRequired(false);

            // Optimistic concurrency. Two workers must never both believe
            // they own the transition of a single order.
            entity.Property(order => order.Version).IsConcurrencyToken().IsRequired();

            entity.HasIndex(order => new { order.UserId, order.CreatedAtUtc });
            entity.HasIndex(order => new { order.UserId, order.RequiresReconciliation });
        });

        modelBuilder.Entity<Position>(entity =>
        {
            entity.ToTable("Positions");
            entity.HasKey(position => position.Id);

            entity.Property(position => position.UserId).IsRequired();
            entity.Property(position => position.StrategyId).IsRequired();
            entity.Property(position => position.Mode).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.HasIndex(position => new { position.UserId, position.Mode });

            entity.Property(position => position.Symbol)
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(position => position.Direction).HasConversion<int>().IsRequired();
            entity.Property(position => position.Status).HasConversion<int>().IsRequired();

            entity.Property(position => position.Quantity).HasColumnType(MoneyColumnType).IsRequired();
            entity.Property(position => position.EntryPrice).HasColumnType(MoneyColumnType).IsRequired();
            entity.Property(position => position.MarkPrice).HasColumnType(MoneyColumnType).IsRequired();
            entity.Property(position => position.UnrealizedPnl).HasColumnType(MoneyColumnType).IsRequired();

            // Protective exit levels are prices and must use the same column
            // type as every other price. Left to the provider default they
            // would be silently truncated, which would move a stop away from
            // where the user placed it.
            entity.Property(position => position.StopLossPrice).HasColumnType(MoneyColumnType);
            entity.Property(position => position.TakeProfitPrice).HasColumnType(MoneyColumnType);

            entity.Property(position => position.OpenedAtUtc).IsRequired();
            entity.Property(position => position.LastTransitionAtUtc).IsRequired(false);
            entity.Property(position => position.Version).IsConcurrencyToken().IsRequired();

            entity.HasIndex(position => new { position.UserId, position.Status });
        });

        modelBuilder.Entity<OrderReconciliationRecord>(entity =>
        {
            entity.ToTable("OrderReconciliations");
            entity.HasKey(record => record.Id);

            entity.Property(record => record.OrderId).IsRequired();

            entity.Property(record => record.ExchangeOrderId)
                .HasMaxLength(64)
                .IsRequired(false);

            entity.Property(record => record.ObservedStatus).HasConversion<int>().IsRequired();
            entity.Property(record => record.ObservedAtUtc).IsRequired();

            entity.Property(record => record.Source)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(record => record.ResolutionReason)
                .HasMaxLength(512)
                .IsRequired(false);

            entity.Property(record => record.ResolvedAtUtc).IsRequired(false);

            entity.HasIndex(record => record.OrderId);
            entity.HasIndex(record => record.ResolvedAtUtc);
        });

        modelBuilder.Entity<PersistedCandle>(entity =>
        {
            entity.ToTable("Candles");
            entity.HasKey(candle => new { candle.Symbol, candle.Interval, candle.OpenTimeUtc });

            entity.Property(candle => candle.Symbol)
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(candle => candle.Interval)
                .HasConversion<int>()
                .IsRequired();

            entity.Property(candle => candle.OpenTimeUtc).IsRequired();
            entity.Property(candle => candle.CloseTimeUtc).IsRequired();
            entity.Property(candle => candle.Open).HasColumnType("decimal(28,12)").IsRequired();
            entity.Property(candle => candle.High).HasColumnType("decimal(28,12)").IsRequired();
            entity.Property(candle => candle.Low).HasColumnType("decimal(28,12)").IsRequired();
            entity.Property(candle => candle.Close).HasColumnType("decimal(28,12)").IsRequired();
            entity.Property(candle => candle.Volume).HasColumnType("decimal(28,12)").IsRequired();
            entity.Property(candle => candle.IsClosed).IsRequired();
            entity.Property(candle => candle.IsDerived).IsRequired();
            entity.Property(candle => candle.QualityFlags).HasColumnType("nvarchar(max)").IsRequired();

            entity.HasIndex(candle => new { candle.Symbol, candle.Interval, candle.CloseTimeUtc, candle.OpenTimeUtc });
        });

        modelBuilder.Entity<PersistedScanRequest>(entity =>
        {
            entity.ToTable("ScanRequests");
            entity.HasKey(request => request.Id);
            entity.Property(request => request.OwnerId).IsRequired();
            entity.Property(request => request.Name).HasMaxLength(200).IsRequired();
            entity.Property(request => request.Symbols).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(request => request.Interval).HasConversion<int>().IsRequired();
            entity.Property(request => request.Criteria).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(request => request.ResultLimit).IsRequired();
            entity.Property(request => request.CreatedAtUtc).IsRequired();
            entity.HasIndex(request => new { request.OwnerId, request.CreatedAtUtc, request.Id });
        });

        modelBuilder.Entity<PersistedScanResult>(entity =>
        {
            entity.ToTable("ScanResults");
            // This identity is the durable idempotency boundary for a scan
            // run: duplicate evidence is harmless, conflicting evidence is not
            // silently allowed to replace the original observation.
            entity.HasKey(result => new { result.ScanRequestId, result.ScanRunId, result.Symbol });
            entity.Property(result => result.OwnerId).IsRequired();
            entity.Property(result => result.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(result => result.Rank).IsRequired();
            entity.Property(result => result.Score).HasColumnType("decimal(18,12)").IsRequired();
            entity.Property(result => result.MatchedCriteria).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(result => result.EvidenceAsOfUtc).IsRequired();
            entity.Property(result => result.EvaluatedAtUtc).IsRequired();
            entity.HasIndex(result => new
            {
                result.OwnerId,
                result.ScanRequestId,
                result.ScanRunId,
                result.Rank,
                result.Score,
                result.Symbol
            });

            modelBuilder.Entity<PersistedHistoricalDataset>(entity =>
            {
                entity.ToTable("HistoricalDatasets");
                entity.HasKey(dataset => dataset.VersionIdentity);
                entity.Property(dataset => dataset.VersionIdentity).HasMaxLength(64).IsRequired();
                entity.Property(dataset => dataset.Id).HasMaxLength(128).IsRequired();
                entity.HasIndex(dataset => dataset.Id).IsUnique();
                entity.Property(dataset => dataset.Source).HasMaxLength(128).IsRequired();
                entity.Property(dataset => dataset.Symbol).HasMaxLength(64).IsRequired();
                entity.Property(dataset => dataset.Interval).HasMaxLength(8).IsRequired();
                entity.Property(dataset => dataset.FromUtc).IsRequired();
                entity.Property(dataset => dataset.ToUtc).IsRequired();
                entity.Property(dataset => dataset.CandleCount).IsRequired();
                entity.Property(dataset => dataset.ContentFingerprint).HasMaxLength(64).IsRequired();
                entity.Property(dataset => dataset.SourceVersion).HasMaxLength(128).IsRequired();
                entity.Property(dataset => dataset.CreatedAtUtc).IsRequired();
                entity.Property(dataset => dataset.ContainsOnlyClosedCandles).IsRequired();
                entity.HasIndex(dataset => new
                {
                    dataset.Symbol,
                    dataset.Interval,
                    dataset.FromUtc,
                    dataset.ToUtc,
                    dataset.CreatedAtUtc,
                    dataset.VersionIdentity
                });
            });
        });

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectHistoricalDatasetChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        RejectHistoricalDatasetChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectHistoricalDatasetChanges()
    {
        ChangeTracker.DetectChanges();
        if (ChangeTracker.Entries<PersistedHistoricalDataset>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Historical dataset manifests are immutable and cannot be changed or deleted.");
        }
    }
}
