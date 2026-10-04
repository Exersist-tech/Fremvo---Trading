using System.Globalization;
using System.Text.Json;
using Trading.MarketData;

namespace Trading.Exchanges.Kraken.MarketData;

public sealed record PublicTickerQuote(string Symbol, decimal Price, DateTimeOffset RetrievedAtUtc);

/// <summary>Public best-bid/ask midpoint for chart display only, not an execution price.</summary>
public sealed class KrakenPublicTickerQuoteSource(HttpClient httpClient)
{
    public async Task<PublicTickerQuote> GetAsync(string symbol, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var response = await httpClient.GetAsync(
            new Uri($"public/Ticker?pair={Uri.EscapeDataString(KrakenV2SymbolNames.ForPublicStream(symbol))}&assetVersion=1",
                UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        using (response)
        {
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return Parse(document.RootElement, symbol, DateTimeOffset.UtcNow);
            }
            catch (JsonException exception)
            {
                throw new MarketDataSourceException("Kraken returned malformed public ticker quote data.", exception);
            }
        }
    }

    internal static PublicTickerQuote Parse(JsonElement root, string symbol, DateTimeOffset retrievedAtUtc)
    {
        try
        {
            if (root.GetProperty("error").GetArrayLength() != 0)
                throw new MarketDataSourceException("Kraken rejected the public ticker quote request.");

            var result = root.GetProperty("result");
            var pairs = result.EnumerateObject().ToArray();
            if (pairs.Length != 1
                || !string.Equals(pairs[0].Name, KrakenV2SymbolNames.ForPublicStream(symbol),
                    StringComparison.OrdinalIgnoreCase))
                throw new MarketDataSourceException("Kraken returned an unexpected public ticker quote.");

            var ticker = pairs[0].Value;
            var bidText = ticker.GetProperty("b")[0].GetString();
            var askText = ticker.GetProperty("a")[0].GetString();
            if (!decimal.TryParse(bidText, NumberStyles.Number, CultureInfo.InvariantCulture, out var bid)
                || !decimal.TryParse(askText, NumberStyles.Number, CultureInfo.InvariantCulture, out var ask)
                || bid <= 0 || ask < bid)
                throw new MarketDataSourceException("Kraken returned an invalid public bid/ask quote.");

            return new PublicTickerQuote(symbol, bid + (ask - bid) / 2m, retrievedAtUtc);
        }
        catch (JsonException exception)
        {
            throw new MarketDataSourceException("Kraken returned malformed public ticker quote data.", exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new MarketDataSourceException("Kraken returned malformed public ticker quote data.", exception);
        }
        catch (IndexOutOfRangeException exception)
        {
            throw new MarketDataSourceException("Kraken returned malformed public ticker quote data.", exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new MarketDataSourceException("Kraken returned malformed public ticker quote data.", exception);
        }
        catch (OverflowException exception)
        {
            throw new MarketDataSourceException("Kraken returned an invalid public bid/ask quote.", exception);
        }
    }
}
