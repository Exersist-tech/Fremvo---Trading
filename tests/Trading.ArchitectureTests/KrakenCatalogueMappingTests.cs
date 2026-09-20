using Trading.Exchanges.Abstractions.Catalogue;
using Trading.Exchanges.Kraken.Catalogue;

namespace Trading.ArchitectureTests;

/// <summary>
/// Covers the only place in the platform that understands Kraken's wire
/// format. The shape used here matches a live <c>AssetPairs</c> response
/// captured from Kraken's public endpoint.
/// </summary>
public sealed class KrakenCatalogueMappingTests
{
    private const string XbtUsdDocument = """
    {
      "error": [],
      "result": {
        "XXBTZUSD": {
          "altname": "XBTUSD",
          "wsname": "XBT/USD",
          "base": "XXBT",
          "quote": "ZUSD",
          "status": "online",
          "lot_decimals": 8,
          "tick_size": "0.1",
          "ordermin": "0.00005",
          "costmin": "0.5",
          "leverage_buy": [2, 3, 4, 5],
          "leverage_sell": [2, 3, 4, 5]
        }
      }
    }
    """;

    [Fact]
    public void MapsALivePairIntoNeutralTerms()
    {
        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(XbtUsdDocument));

        Assert.Equal("XBTUSD", entry.ExchangeSymbol);
        Assert.Equal("XBT", entry.BaseAsset);
        Assert.Equal("USD", entry.QuoteAsset);
        Assert.Equal(CatalogueTradingStatus.Trading, entry.Status);
        Assert.Equal("ONLINE", entry.ExchangeStatusRaw);
        Assert.True(entry.HasCompleteFilters);
    }

    /// <summary>
    /// Trading rules must survive as exact decimals. A step size that drifts
    /// changes the size of a real order.
    /// </summary>
    [Fact]
    public void TradingRulesAreExactDecimals()
    {
        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(XbtUsdDocument));

        Assert.Equal(0.1m, entry.PriceTickSize);
        Assert.Equal(0.00000001m, entry.QuantityStepSize);
        Assert.Equal(0.00005m, entry.MinimumQuantity);
        Assert.Equal(0.5m, entry.MinimumNotional);
    }

    /// <summary>
    /// Leverage must surface as a separate capability. It must never appear
    /// as a property of spot trading, or enabling spot would enable leverage.
    /// </summary>
    [Fact]
    public void LeverageIsASeparateCapabilityFromSpot()
    {
        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(XbtUsdDocument));

        Assert.Contains("SPOT", entry.Permissions);
        Assert.Contains("MARGIN", entry.Permissions);
    }

    [Fact]
    public void PairWithoutLeverageListsIsSpotOnly()
    {
        const string json = """
        {
          "error": [],
          "result": {
            "PEPEUSD": {
              "altname": "PEPEUSD",
              "wsname": "PEPE/USD",
              "base": "PEPE",
              "quote": "ZUSD",
              "status": "online",
              "lot_decimals": 2,
              "tick_size": "0.0000001",
              "ordermin": "1000",
              "costmin": "0.5",
              "leverage_buy": [],
              "leverage_sell": []
            }
          }
        }
        """;

        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(json));

        Assert.Contains("SPOT", entry.Permissions);
        Assert.DoesNotContain("MARGIN", entry.Permissions);
        Assert.Equal(0.01m, entry.QuantityStepSize);
    }

    /// <summary>
    /// Kraken answers with HTTP 200 even when the call failed, reporting the
    /// failure only in the error array. Ignoring it would turn an outage into
    /// an apparently successful, empty catalogue, which the universe would
    /// read as a mass delisting.
    /// </summary>
    [Fact]
    public void ReportedErrorIsAFailureNotAnEmptyCatalogue()
    {
        const string json = """
        { "error": ["EGeneral:Temporary lockout"], "result": {} }
        """;

        var exception = Assert.Throws<FormatException>(() => KrakenAssetPairMapper.MapDocument(json));
        Assert.Contains("EGeneral:Temporary lockout", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedDocumentIsAFailureNotAnEmptyCatalogue()
    {
        Assert.Throws<FormatException>(() => KrakenAssetPairMapper.MapDocument("{ not json"));
        Assert.Throws<FormatException>(() => KrakenAssetPairMapper.MapDocument("null"));
    }

    [Fact]
    public void MissingResultElementIsAFailure()
    {
        Assert.Throws<FormatException>(() => KrakenAssetPairMapper.MapDocument("""{ "error": [] }"""));
    }

    /// <summary>
    /// Every restricted status must map to its own value, and a status Kraken
    /// introduces later must never be read as permission to trade.
    /// </summary>
    [Theory]
    [InlineData("online", CatalogueTradingStatus.Trading)]
    [InlineData("limit_only", CatalogueTradingStatus.LimitOnly)]
    [InlineData("post_only", CatalogueTradingStatus.PostOnly)]
    [InlineData("reduce_only", CatalogueTradingStatus.ReduceOnly)]
    [InlineData("cancel_only", CatalogueTradingStatus.CancelOnly)]
    [InlineData("maintenance", CatalogueTradingStatus.Halted)]
    [InlineData("delisted", CatalogueTradingStatus.Delisted)]
    [InlineData("something_kraken_adds_later", CatalogueTradingStatus.Unknown)]
    public void StatusVocabularyIsTranslatedExhaustively(string krakenStatus, CatalogueTradingStatus expected)
    {
        var json = $$"""
        {
          "error": [],
          "result": {
            "XETHZUSD": {
              "altname": "ETHUSD",
              "wsname": "ETH/USD",
              "base": "XETH",
              "quote": "ZUSD",
              "status": "{{krakenStatus}}",
              "lot_decimals": 8,
              "tick_size": "0.01",
              "ordermin": "0.002",
              "costmin": "0.5"
            }
          }
        }
        """;

        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(json));

        Assert.Equal(expected, entry.Status);
    }

    /// <summary>
    /// The prefix strip applies only to Kraken's four-character legacy codes.
    /// XRP and ZEC are real assets whose first letter must survive.
    /// </summary>
    [Fact]
    public void ShortAssetCodesKeepTheirLeadingLetter()
    {
        const string json = """
        {
          "error": [],
          "result": {
            "ZECUSD": {
              "altname": "ZECUSD",
              "base": "ZEC",
              "quote": "ZUSD",
              "status": "online",
              "lot_decimals": 8,
              "tick_size": "0.01",
              "ordermin": "0.03",
              "costmin": "0.5"
            }
          }
        }
        """;

        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(json));

        Assert.Equal("ZEC", entry.BaseAsset);
        Assert.Equal("USD", entry.QuoteAsset);
    }

    /// <summary>
    /// A rule the exchange did not supply must read as unknown, so the
    /// filters gate fails closed rather than sizing an order against a
    /// substituted default.
    /// </summary>
    [Fact]
    public void AbsentOrZeroRulesAreUnknownNotDefaulted()
    {
        const string json = """
        {
          "error": [],
          "result": {
            "SOLUSD": {
              "altname": "SOLUSD",
              "wsname": "SOL/USD",
              "base": "SOL",
              "quote": "ZUSD",
              "status": "online",
              "costmin": "0"
            }
          }
        }
        """;

        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(json));

        Assert.Null(entry.PriceTickSize);
        Assert.Null(entry.QuantityStepSize);
        Assert.Null(entry.MinimumQuantity);
        Assert.Null(entry.MinimumNotional);
        Assert.False(entry.HasCompleteFilters);
    }

    /// <summary>
    /// Kraken supplies no listing date, so listing age must stay unknown.
    /// Substituting a date would grant a new listing a false history and let
    /// it pass the new-listing gate.
    /// </summary>
    [Fact]
    public void ListingDateIsUnknownRatherThanInvented()
    {
        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(XbtUsdDocument));

        Assert.Null(entry.OnboardUtc);
    }

    /// <summary>
    /// A pair we cannot identify must be skipped, never guessed at.
    /// </summary>
    [Fact]
    public void UnidentifiablePairsAreSkipped()
    {
        const string json = """
        {
          "error": [],
          "result": {
            "NOSTATUS": { "altname": "NOSTATUS", "base": "AAA", "quote": "ZUSD" },
            "NOASSETS": { "altname": "NOASSETS", "status": "online" },
            "GOODPAIR": {
              "altname": "ADAUSD",
              "wsname": "ADA/USD",
              "base": "ADA",
              "quote": "ZUSD",
              "status": "online"
            }
          }
        }
        """;

        var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(json));

        Assert.Equal("ADAUSD", entry.ExchangeSymbol);
    }

    /// <summary>
    /// Trading rules are decoded with the invariant culture, so a host locale
    /// using a comma decimal separator cannot change what a rule means.
    /// </summary>
    [Fact]
    public void DecimalParsingIsCultureIndependent()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nb-NO");

            var entry = Assert.Single(KrakenAssetPairMapper.MapDocument(XbtUsdDocument));

            Assert.Equal(0.1m, entry.PriceTickSize);
            Assert.Equal(0.5m, entry.MinimumNotional);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
