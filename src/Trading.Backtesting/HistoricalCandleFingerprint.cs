using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trading.MarketData;

namespace Trading.Backtesting;

/// <summary>
/// Computes the SHA-256 content fingerprint for a dataset's complete ordered
/// candle payload. Each line is one candle and contains, in order, symbol,
/// interval, UTC open and close timestamps (round-trip format), OHLCV decimals
/// (G29/invariant format), closed/derived flags (0 or 1), and ordered numeric
/// quality flags, separated by U+001F. Lines end with U+000A.
/// </summary>
public static class HistoricalCandleFingerprint
{
    public static string Compute(IReadOnlyList<Candle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        var canonical = new StringBuilder();
        foreach (var candle in candles)
        {
            ArgumentNullException.ThrowIfNull(candle);
            canonical.Append(candle.Symbol).Append('\u001F')
                .Append((int)candle.Interval).Append('\u001F')
                .Append(candle.OpenTimeUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.CloseTimeUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.Open.ToString("G29", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.High.ToString("G29", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.Low.ToString("G29", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.Close.ToString("G29", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.Volume.ToString("G29", CultureInfo.InvariantCulture)).Append('\u001F')
                .Append(candle.IsClosed ? '1' : '0').Append('\u001F')
                .Append(candle.IsDerived ? '1' : '0').Append('\u001F')
                .AppendJoin(',', candle.QualityFlags.OrderBy(flag => (int)flag).Select(flag => ((int)flag).ToString(CultureInfo.InvariantCulture)))
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
