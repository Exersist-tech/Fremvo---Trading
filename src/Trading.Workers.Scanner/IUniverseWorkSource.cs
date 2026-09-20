using Trading.Application.Universe;
using Trading.Domain.Universe;

namespace Trading.Workers.Scanner;

/// <summary>
/// Supplies the instruments, eligibility records, and scopes for a
/// recalculation pass.
/// </summary>
/// <remarks>
/// Persistence is not yet implemented, so the host binds an unconfigured
/// provider that yields nothing. It never invents instruments: fabricating a
/// universe would make the worker look healthy while granting eligibility on
/// evidence that does not exist.
/// </remarks>
public interface IUniverseWorkSource
{
    Task<UniverseWorkSet> GetWorkAsync(CancellationToken cancellationToken);

    Task SaveAsync(RecalculationReport report, CancellationToken cancellationToken);
}

public sealed record UniverseWorkSet(
    IReadOnlyCollection<Instrument> Instruments,
    IReadOnlyDictionary<Guid, InstrumentEligibility> Eligibility,
    IReadOnlyCollection<EligibilityScope> Scopes);

/// <summary>
/// A work source that reports an empty universe because no store is
/// configured.
/// </summary>
public sealed class UnconfiguredUniverseWorkSource : IUniverseWorkSource
{
    public Task<UniverseWorkSet> GetWorkAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new UniverseWorkSet(
            Array.Empty<Instrument>(),
            new Dictionary<Guid, InstrumentEligibility>(),
            Array.Empty<EligibilityScope>()));

    public Task SaveAsync(RecalculationReport report, CancellationToken cancellationToken) => Task.CompletedTask;
}
