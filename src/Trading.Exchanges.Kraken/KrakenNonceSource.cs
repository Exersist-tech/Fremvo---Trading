using System.Globalization;

namespace Trading.Exchanges.Kraken;

/// <summary>
/// Supplies Kraken request nonces that always increase.
/// </summary>
/// <remarks>
/// <para>
/// Kraken requires the nonce on a private request to be strictly greater than
/// the previous nonce used with the same key, and rejects anything else with
/// <c>EAPI:Invalid nonce</c>. Deriving the nonce from the clock alone is not
/// enough: a single permission probe makes three private calls in sequence, so
/// two of them can land in the same clock tick and produce the same value.
/// </para>
/// <para>
/// The value is kept on the scale of microseconds rather than milliseconds so
/// that ordinary clock movement still dominates, and a counter guarantees the
/// sequence advances even when the clock does not. The state is an instance
/// field rather than a static so the type can be registered once and tested in
/// isolation.
/// </para>
/// </remarks>
public interface IKrakenNonceSource
{
    /// <summary>Returns a nonce strictly greater than every nonce returned before it.</summary>
    string NextNonce();
}

/// <inheritdoc />
public sealed class KrakenNonceSource : IKrakenNonceSource
{
    private readonly TimeProvider _timeProvider;
    private long _last;

    public KrakenNonceSource(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public string NextNonce()
    {
        var candidate = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1000L;

        // Compare-and-swap rather than a lock: the only requirement is that no
        // two callers observe the same value and that the sequence never goes
        // backwards, including when the clock does.
        long previous;
        long next;
        do
        {
            previous = Interlocked.Read(ref _last);
            next = candidate > previous ? candidate : previous + 1;
        }
        while (Interlocked.CompareExchange(ref _last, next, previous) != previous);

        return next.ToString(CultureInfo.InvariantCulture);
    }
}
