extern alias WebApp;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using System.Net;
using Trading.Domain.Market;
using Trading.MarketData;
using ChartIndicatorOverlayService = WebApp::Trading.Web.Charting.ChartIndicatorOverlayService;

namespace Trading.ArchitectureTests;

public sealed class ChartIndicatorOverlayTests
{
    private static readonly string[] s_smaAndEma = ["sma", "ema"];
    private static readonly string[] s_sma = ["sma"];
    private static readonly string[] s_ema = ["ema"];
    private static readonly decimal[] s_expectedOverlayValues = [12m, 14m];

    [Fact]
    public void OverlaysUseOnlyChronologicalClosedCandlesAndReturnReferenceValues()
    {
        var candles = Candles(10m, 12m, 14m, 16m).ToList();
        candles.Add(Candle(4, 1000m, isClosed: false));

        var overlays = new ChartIndicatorOverlayService().Calculate(candles, s_smaAndEma, 3);

        Assert.Collection(
            overlays,
            sma =>
            {
                Assert.Equal("Ready", sma.Status);
                Assert.Equal(s_expectedOverlayValues, sma.Points.Select(point => point.Value));
                Assert.DoesNotContain(sma.Points, point => point.OpenTimeUtc == candles[4].OpenTimeUtc);
            },
            ema =>
            {
                Assert.Equal("Ready", ema.Status);
                Assert.Equal(s_expectedOverlayValues, ema.Points.Select(point => point.Value));
                Assert.DoesNotContain(ema.Points, point => point.OpenTimeUtc == candles[4].OpenTimeUtc);
            });
    }

    [Fact]
    public void OverlayReportsUnsafeAndMissingHistoryExplicitly()
    {
        var unsafeCandles = Candles(10m, 12m, 14m).ToArray();
        unsafeCandles[1] = Candle(1, 12m, qualityFlags: [DataQualityIssue.Stale]);

        var unsafeOverlay = new ChartIndicatorOverlayService().Calculate(unsafeCandles, s_sma, 3).Single();
        var missingOverlay = new ChartIndicatorOverlayService().Calculate(Candles(10m, 12m), s_ema, 3).Single();

        Assert.Equal("UnsafeHistory", unsafeOverlay.Status);
        Assert.Empty(unsafeOverlay.Points);
        Assert.Equal("InsufficientHistory", missingOverlay.Status);
        Assert.Empty(missingOverlay.Points);
    }

    [Fact]
    public async Task CandleAndIndicatorEndpointRequiresAuthentication()
    {
        await using var factory = new ChartWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(new Uri("/api/marketdata/candles?symbol=XBTUSD&interval=OneHour&indicators=sma", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Candle[] Candles(params decimal[] closes) =>
        closes.Select((close, index) => Candle(index, close)).ToArray();

    private static Candle Candle(
        int index,
        decimal close,
        bool isClosed = true,
        IReadOnlyCollection<DataQualityIssue>? qualityFlags = null)
    {
        var open = DateTimeOffset.UnixEpoch.AddHours(index);
        return new Candle(
            "BTC/USD",
            CandleInterval.OneHour,
            open,
            open.AddHours(1),
            close,
            close,
            close,
            close,
            1m,
            isClosed,
            false,
            qualityFlags);
    }

    private sealed class ChartWebApplicationFactory : WebApplicationFactory<WebApp::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Development:SeedDemoData"] = "false",
                    ["ConnectionStrings:TradingDb"] = "Server=(localdb)\\MSSQLLocalDB;Database=ChartIndicatorOverlayTests;Trusted_Connection=True;"
                }));
        }
    }
}
