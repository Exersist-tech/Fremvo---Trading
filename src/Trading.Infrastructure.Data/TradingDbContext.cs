using Microsoft.EntityFrameworkCore;
using Trading.Domain.Audit;
using Trading.Domain.Users;
using Trading.Exchanges.Abstractions;

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

            entity.HasIndex(account => new { account.UserId, account.ExchangeKind })
                .IsUnique(false);
        });

        base.OnModelCreating(modelBuilder);
    }
}
