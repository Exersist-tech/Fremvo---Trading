using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Domain.Execution;
using Trading.Domain.Orders;
using Trading.Domain.Positions;
using Trading.Domain.Users;
using Trading.Exchanges.Abstractions;
using Trading.Infrastructure.Data.MarketData;

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

        base.OnModelCreating(modelBuilder);
    }
}
