using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Trading.Exchanges.Abstractions;
using Trading.Exchanges.Abstractions.Account;

namespace Trading.Exchanges.Kraken.Account;

/// <summary>Read-only Kraken Spot balance client.</summary>
public sealed class KrakenBalanceGateway : IExchangeBalanceGateway
{
    internal const string BalancePath = "/0/private/BalanceEx";

    private readonly HttpClient _httpClient;
    private readonly IKrakenNonceSource _nonceSource;
    private readonly TimeProvider _timeProvider;

    public KrakenBalanceGateway(HttpClient httpClient, IKrakenNonceSource nonceSource, TimeProvider timeProvider)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _nonceSource = nonceSource ?? throw new ArgumentNullException(nameof(nonceSource));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ExchangeKind Exchange => ExchangeKind.Kraken;

    public async Task<ExchangeBalanceSnapshot> ReadBalancesAsync(
        ExchangeCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var nonce = _nonceSource.NextNonce();
        var body = $"nonce={nonce}";
        string signature;
        try
        {
            signature = KrakenRequestSigner.Sign(BalancePath, nonce, body, credential.ApiSecret);
        }
        catch (FormatException exception)
        {
            throw new ExchangeBalanceReadException("Kraken rejected the credential format.", exception);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BalancePath)
        {
            Content = new StringContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        request.Headers.TryAddWithoutValidation("API-Key", credential.ApiKey);
        request.Headers.TryAddWithoutValidation("API-Sign", signature);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ExchangeBalanceReadException("Kraken did not accept the balance request.");
            }

            return Parse(payload, _timeProvider.GetUtcNow());
        }
        catch (HttpRequestException exception)
        {
            throw new ExchangeBalanceReadException("Kraken could not be reached for a balance reading.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExchangeBalanceReadException("The Kraken balance request timed out.", exception);
        }
    }

    internal static ExchangeBalanceSnapshot Parse(string payload, DateTimeOffset retrievedAtUtc)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("error", out var errors) || errors.ValueKind != JsonValueKind.Array)
            {
                throw new ExchangeBalanceReadException("Kraken returned an unreadable balance response.");
            }

            if (errors.GetArrayLength() > 0)
            {
                throw new ExchangeBalanceReadException("Kraken declined the balance reading.");
            }

            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                throw new ExchangeBalanceReadException("Kraken returned no balances.");
            }

            var balances = new List<ExchangeBalance>();
            var assets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in result.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new ExchangeBalanceReadException("Kraken returned an invalid extended balance.");

                var total = ReadAmount(property.Value, "balance", required: true);
                var held = ReadAmount(property.Value, "hold_trade", required: true);
                var credit = ReadAmount(property.Value, "credit", required: false);
                var usedCredit = ReadAmount(property.Value, "credit_used", required: false);
                if (held < 0m || credit < 0m || usedCredit < 0m)
                    throw new ExchangeBalanceReadException("Kraken returned an invalid credit or trade hold.");

                var asset = NormalizeAsset(property.Name);
                if (string.IsNullOrWhiteSpace(asset) || !assets.Add(asset))
                    throw new ExchangeBalanceReadException("Kraken returned an ambiguous balance asset.");

                try
                {
                    balances.Add(new ExchangeBalance(asset, total,
                        total + credit - usedCredit - held, held, property.Name));
                }
                catch (OverflowException exception)
                {
                    throw new ExchangeBalanceReadException("Kraken returned an unrepresentable balance.", exception);
                }
            }

            return new ExchangeBalanceSnapshot(retrievedAtUtc, balances);
        }
        catch (JsonException exception)
        {
            throw new ExchangeBalanceReadException("Kraken returned an unreadable balance response.", exception);
        }
    }

    private static decimal ReadAmount(JsonElement entry, string name, bool required)
    {
        if (!entry.TryGetProperty(name, out var value))
        {
            if (!required)
                return 0m;
            throw new ExchangeBalanceReadException("Kraken omitted a required extended balance amount.");
        }
        if (value.ValueKind != JsonValueKind.String
            || !decimal.TryParse(value.GetString(),
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var amount))
            throw new ExchangeBalanceReadException("Kraken returned an invalid extended balance amount.");
        return amount;
    }

    internal static string NormalizeAsset(string asset) => asset.ToUpperInvariant() switch
    {
        "XXBT" or "XBT" => "BTC",
        "XDG" => "DOGE",
        var code when code.Length == 4 && (code[0] == 'X' || code[0] == 'Z') => code[1..],
        var code => code
    };
}
