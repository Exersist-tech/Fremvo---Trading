using Trading.Domain.Universe;

namespace Trading.Application.Universe;

/// <summary>
/// Supplies the tracked instruments.
/// </summary>
public interface IUniverseInstrumentProvider
{
    Task<IReadOnlyCollection<Instrument>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Serves the configured research seed until instrument persistence exists.
/// </summary>
/// <remarks>
/// The instruments it returns have never been observed in the exchange
/// catalogue, so every gate that depends on the exchange fails and nothing is
/// eligible. That is truthful rather than convenient: without a catalogue
/// synchronisation the platform genuinely does not know these symbols are
/// tradable.
///
/// Ids are derived deterministically from the symbol so a restart does not
/// silently create a second identity for the same instrument.
/// </remarks>
public sealed class SeedUniverseInstrumentProvider : IUniverseInstrumentProvider
{
    private readonly IReadOnlyCollection<Instrument> _instruments =
        MarketUniverseSeed.Create(DeterministicId);

    public Task<IReadOnlyCollection<Instrument>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_instruments);

    private static Guid DeterministicId(string symbol)
    {
        var bytes = new byte[16];
        var source = System.Text.Encoding.UTF8.GetBytes(
            MarketUniverseSeed.ExchangeName + ":" + symbol);

        for (var i = 0; i < source.Length; i++)
        {
            bytes[i % 16] = (byte)(bytes[i % 16] * 31 + source[i]);
        }

        // Guarantee a non-empty id: the Instrument constructor rejects one.
        bytes[0] |= 0x01;
        return new Guid(bytes);
    }
}
