using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Entitlements;
using Trading.Domain.Audit;
using Trading.Domain.Entitlements;
using Trading.Domain.Identity;
using Trading.Domain.Users;

namespace Trading.Infrastructure.Data.Entitlements;

public sealed class EfEntitlementRepository(TradingDbContext db, TimeProvider time)
    : IEntitlementRepository
{
    public Task<bool> IsOwnerActiveAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("An owner is required.", nameof(userId));
        return db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == userId && user.Status == UserStatus.Active, cancellationToken);
    }

    public Task<Plan?> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default)
    {
        if (planId == Guid.Empty)
            throw new ArgumentException("A plan id is required.", nameof(planId));
        return db.Plans.AsNoTracking().SingleOrDefaultAsync(plan => plan.Id == planId, cancellationToken);
    }

    public Task<Entitlement?> GetEffectiveAsync(
        Guid userId, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || asOfUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An owner and UTC instant are required.");
        return db.Entitlements.AsNoTracking()
            .SingleOrDefaultAsync(value => value.UserId == userId
                && value.Status == EntitlementStatus.Active && value.AssignedAtUtc <= asOfUtc
                && (value.TrialExpiresAtUtc == null || value.TrialExpiresAtUtc > asOfUtc)
                && db.Users.Any(user => user.Id == value.UserId && user.Status == UserStatus.Active),
                cancellationToken);
    }

    public async Task AddPlanAsync(
        Plan plan, Guid administratorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await VerifyAdministratorAsync(administratorId, cancellationToken).ConfigureAwait(false);
        if (await db.Plans.AnyAsync(existing => existing.Code == plan.Code, cancellationToken)
            .ConfigureAwait(false))
            throw new InvalidOperationException("A plan with this code already exists.");
        db.Plans.Add(plan);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), administratorId, "Entitlements.PlanCreated",
            "Plan", plan.Id.ToString("D"), time.GetUtcNow(), null,
            JsonSerializer.Serialize(new { plan.Code, plan.MaxExperimentWorkers,
                plan.LiveTradingEligible, plan.FuturesEligible }), Guid.NewGuid().ToString("D")));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AssignAsync(
        Entitlement entitlement, Guid administratorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        await VerifyAdministratorAsync(administratorId, cancellationToken).ConfigureAwait(false);
        if (!await db.Users.AnyAsync(user => user.Id == entitlement.UserId && user.Status == UserStatus.Active,
                cancellationToken).ConfigureAwait(false)
            || !await db.Plans.AnyAsync(plan => plan.Id == entitlement.PlanId, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An active entitlement owner and plan must exist.");
        if (entitlement.Status != EntitlementStatus.Active
            || await db.Entitlements.AnyAsync(value => value.UserId == entitlement.UserId
                && value.Status == EntitlementStatus.Active, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Revoke the existing active entitlement before assigning another.");
        db.Entitlements.Add(entitlement);
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), administratorId, "Entitlements.Assigned",
            "Entitlement", entitlement.Id.ToString("D"), time.GetUtcNow(), null,
            JsonSerializer.Serialize(new { entitlement.UserId, entitlement.PlanId,
                entitlement.TrialExpiresAtUtc }), Guid.NewGuid().ToString("D")));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeAsync(Guid userId, Guid entitlementId, Guid administratorId,
        CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || entitlementId == Guid.Empty)
            throw new ArgumentException("An owner and entitlement id are required.");
        await VerifyAdministratorAsync(administratorId, cancellationToken).ConfigureAwait(false);
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var entitlement = await db.Entitlements.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == entitlementId && value.UserId == userId
                && value.Status == EntitlementStatus.Active, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No active entitlement belongs to this owner.");
        var changed = await db.Entitlements.Where(value => value.Id == entitlementId
            && value.UserId == userId && value.Status == EntitlementStatus.Active)
            .ExecuteUpdateAsync(setter => setter.SetProperty(value => value.Status, EntitlementStatus.Revoked),
                cancellationToken).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException("The entitlement changed during revocation.");
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), administratorId, "Entitlements.Revoked",
            "Entitlement", entitlement.Id.ToString("D"), time.GetUtcNow(),
            JsonSerializer.Serialize(new { entitlement.UserId, entitlement.PlanId }),
            JsonSerializer.Serialize(new { Status = EntitlementStatus.Revoked }),
            Guid.NewGuid().ToString("D")));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExtendTrialAsync(Guid userId, Guid entitlementId, DateTimeOffset newExpiryUtc,
        Guid administratorId, CancellationToken cancellationToken = default)
    {
        if (userId == Guid.Empty || entitlementId == Guid.Empty)
            throw new ArgumentException("An owner and entitlement id are required.");
        await VerifyAdministratorAsync(administratorId, cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var entitlement = await db.Entitlements.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == entitlementId && value.UserId == userId
                && value.Status == EntitlementStatus.Active
                && db.Users.Any(user => user.Id == value.UserId && user.Status == UserStatus.Active)
                && db.Plans.Any(plan => plan.Id == value.PlanId
                    && !plan.LiveTradingEligible && !plan.FuturesEligible),
                cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Only an active owner's paper-plan trial can be renewed.");
        var previousExpiry = entitlement.TrialExpiresAtUtc;
        entitlement.ExtendTrial(now, newExpiryUtc);

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        var changed = await db.Entitlements.Where(value => value.Id == entitlementId
            && value.UserId == userId && value.Status == EntitlementStatus.Active
            && value.TrialExpiresAtUtc == previousExpiry
            && db.Users.Any(user => user.Id == value.UserId && user.Status == UserStatus.Active)
            && db.Plans.Any(plan => plan.Id == value.PlanId
                && !plan.LiveTradingEligible && !plan.FuturesEligible))
            .ExecuteUpdateAsync(setter => setter.SetProperty(value => value.TrialExpiresAtUtc, newExpiryUtc),
                cancellationToken).ConfigureAwait(false);
        if (changed != 1)
            throw new InvalidOperationException("The trial changed during renewal.");
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), administratorId, "Entitlements.TrialExtended",
            "Entitlement", entitlementId.ToString("D"), now,
            JsonSerializer.Serialize(new { entitlement.UserId, PreviousExpiryUtc = previousExpiry }),
            JsonSerializer.Serialize(new { NewExpiryUtc = newExpiryUtc }),
            Guid.NewGuid().ToString("D")));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyAdministratorAsync(Guid administratorId, CancellationToken cancellationToken)
    {
        if (administratorId == Guid.Empty || !await db.Users.AsNoTracking().AnyAsync(user =>
            user.Id == administratorId && user.Role == RoleType.Administrator
                && user.MultiFactorAuthenticationEnabled && user.Status == UserStatus.Active,
            cancellationToken).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Only an active MFA-enrolled administrator may change entitlements.");
    }
}
