using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trading.Application.Experiments;
using Trading.Domain.Audit;

namespace Trading.Infrastructure.Data.Experiments;

/// <summary>Owner-scoped, append-only evidence in the existing audit ledger.</summary>
public sealed class EfPaperScanUniverseSnapshotRepository(TradingDbContext db)
{
    private const string Action = "PaperScanner.UniverseObserved";
    private const string TargetType = "PaperScanUniverse";

    public async Task EnqueueIfNewAsync(
        PaperScanUniverseSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        var existing = await GetAsync(snapshot.OwnerId, snapshot.SignalBoundaryUtc, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Fingerprint != snapshot.Fingerprint
                || !existing.Members.SequenceEqual(snapshot.Members))
                throw new InvalidOperationException("A scan boundary already has different universe evidence.");
            return;
        }

        db.AuditEvents.Add(new AuditEvent(
            EvidenceId(snapshot.OwnerId, snapshot.SignalBoundaryUtc),
            snapshot.OwnerId, Action, TargetType,
            snapshot.SignalBoundaryUtc.ToString("O"),
            snapshot.ObservedAtUtc, null,
            JsonSerializer.Serialize(snapshot), snapshot.Fingerprint));
    }

    public async Task<PaperScanUniverseSnapshot?> GetAsync(
        Guid ownerId,
        DateTimeOffset signalBoundaryUtc,
        CancellationToken cancellationToken = default)
    {
        if (ownerId == Guid.Empty || signalBoundaryUtc.Offset != TimeSpan.Zero
            || signalBoundaryUtc == default
            || signalBoundaryUtc.UtcTicks % TimeSpan.FromMinutes(5).Ticks != 0)
            throw new ArgumentException("An owner and aligned UTC scan boundary are required.");
        var row = await db.AuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == EvidenceId(ownerId, signalBoundaryUtc),
                cancellationToken).ConfigureAwait(false);
        if (row is null)
            return null;
        if (row.ActorUserId != ownerId || row.Action != Action
            || row.TargetType != TargetType
            || row.TargetId != signalBoundaryUtc.ToString("O")
            || string.IsNullOrWhiteSpace(row.After))
            throw new InvalidOperationException("Stored scan universe does not match its owner and boundary.");
        PaperScanUniverseSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<PaperScanUniverseSnapshot>(row.After)
                ?? throw new InvalidOperationException("Stored scan universe has no evidence payload.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored scan universe evidence is malformed.", exception);
        }
        snapshot.Validate();
        if (snapshot.OwnerId != ownerId || snapshot.SignalBoundaryUtc != signalBoundaryUtc
            || snapshot.ObservedAtUtc != row.OccurredAtUtc
            || snapshot.Fingerprint != row.CorrelationId)
            throw new InvalidOperationException("Stored scan universe does not match its immutable audit envelope.");
        return snapshot with
        {
            Members = Array.AsReadOnly(snapshot.Members.ToArray()),
            DailyEvidence = Array.AsReadOnly(snapshot.DailyEvidence.ToArray()),
            SeriesEvidence = snapshot.SeriesEvidence is null
                ? null : Array.AsReadOnly(snapshot.SeriesEvidence.ToArray()),
            StrategyEvidence = snapshot.StrategyEvidence is null
                ? null : Array.AsReadOnly(snapshot.StrategyEvidence.ToArray())
        };
    }

    private static Guid EvidenceId(Guid ownerId, DateTimeOffset boundary) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"PaperScanner.UniverseObserved|{ownerId:D}|{boundary:O}")).AsSpan(0, 16));
}
