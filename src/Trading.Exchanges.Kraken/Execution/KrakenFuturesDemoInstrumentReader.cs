using System.Text.Json;

namespace Trading.Exchanges.Kraken.Execution;

/// <summary>
/// Public demo-venue instrument facts. ContractSize is the venue's raw field,
/// not a verified base-asset multiplier for exposure calculations.
/// </summary>
public sealed record KrakenFuturesDemoInstrument(
    string Symbol,
    string Pair,
    string Quote,
    decimal ContractSize,
    decimal TickSize,
    int ContractValueTradePrecision,
    decimal MaxPositionSize,
    DateTimeOffset ObservedAtUtc);

/// <summary>Reads public contract specifications without credentials or order access.</summary>
public sealed class KrakenFuturesDemoInstrumentReader
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly Uri _instrumentsUri;
    private readonly TimeProvider _clock;

    public KrakenFuturesDemoInstrumentReader(
        HttpClient httpClient,
        KrakenFuturesDemoOptions options,
        TimeProvider? clock = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);
        _instrumentsUri = new Uri(options.BaseUri, "instruments?contractType=flexible_futures&expired=false");
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<KrakenFuturesDemoInstrument> GetAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.Length <= 3 || !symbol.StartsWith("PF_", StringComparison.Ordinal)
            || symbol[3..].Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character)))
            throw new ArgumentException("A Kraken linear perpetual symbol is required.", nameof(symbol));
        if (_httpClient.DefaultRequestHeaders.Authorization is not null
            || _httpClient.DefaultRequestHeaders.Contains("APIKey"))
            throw new InvalidOperationException("Public demo instrument requests require an unauthenticated HTTP client.");

        using var response = await _httpClient.GetAsync(
            _instrumentsUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri != _instrumentsUri)
            throw new InvalidDataException("The demo instrument request left the expected endpoint.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("The demo instrument response exceeds the supported size.");

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await using (stream.ConfigureAwait(false))
        {
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw new InvalidDataException("The demo instrument response exceeds the supported size.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.String
            || result.GetString() != "success"
            || !root.TryGetProperty("instruments", out var instruments)
            || instruments.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The demo venue did not provide a successful instrument snapshot.");

        JsonElement? match = null;
        foreach (var instrument in instruments.EnumerateArray())
        {
            if (instrument.ValueKind != JsonValueKind.Object
                || !instrument.TryGetProperty("symbol", out var candidate)
                || candidate.ValueKind != JsonValueKind.String
                || candidate.GetString() != symbol)
                continue;
            if (match.HasValue)
                throw new InvalidDataException("The demo venue returned duplicate instrument specifications.");
            match = instrument;
        }
        if (!match.HasValue)
            throw new InvalidDataException("The requested demo instrument is not listed.");

        var item = match.Value;
        if (RequiredString(item, "type") != "flexible_futures"
            || RequiredBoolean(item, "isExpired")
            || !RequiredBoolean(item, "tradeable")
            || RequiredBoolean(item, "tradfi")
            || (item.TryGetProperty("lastTradingTime", out var expiry)
                && expiry.ValueKind != JsonValueKind.Null))
            throw new InvalidDataException("The demo instrument is not an active crypto perpetual.");

        var quote = RequiredString(item, "quote");
        var baseAsset = RequiredString(item, "base");
        var pair = RequiredString(item, "pair");
        if (quote.Any(character => !char.IsAsciiLetterUpper(character))
            || baseAsset.Any(character => !char.IsAsciiLetterUpper(character) && !char.IsAsciiDigit(character))
            || pair != baseAsset + ":" + quote)
            throw new InvalidDataException("The demo instrument's quote and pair are inconsistent.");

        var size = RequiredPositiveDecimal(item, "contractSize");
        var tick = RequiredPositiveDecimal(item, "tickSize");
        var maximum = RequiredPositiveDecimal(item, "maxPositionSize");
        if (!item.TryGetProperty("contractValueTradePrecision", out var precisionElement)
            || precisionElement.ValueKind != JsonValueKind.Number
            || !precisionElement.TryGetInt32(out var precision) || precision is < 0 or > 28)
            throw new InvalidDataException("The demo instrument has no valid trade precision.");

        return new KrakenFuturesDemoInstrument(symbol, pair, quote, size, tick, precision,
            maximum, _clock.GetUtcNow());
    }

    private static string RequiredString(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"The demo instrument has no valid {name}.");
        return value.GetString()!;
    }

    private static bool RequiredBoolean(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"The demo instrument has no valid {name}.");
        return value.GetBoolean();
    }

    private static decimal RequiredPositiveDecimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var number) || number <= 0m)
            throw new InvalidDataException($"The demo instrument has no valid {name}.");
        return number;
    }
}
