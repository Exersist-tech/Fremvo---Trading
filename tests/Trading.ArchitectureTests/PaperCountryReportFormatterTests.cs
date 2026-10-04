extern alias WebApp;

using Trading.Application.Reporting;
using Trading.Domain.Experiments;
using Trading.Domain.Reporting;

namespace Trading.ArchitectureTests;

public sealed class PaperCountryReportFormatterTests
{
    [Fact]
    public void GbExampleUsesSelectedIanaZoneAndDstWithoutInventingTaxOrFxValues()
    {
        var profile = new ReportingProfile("fr-FR", "Europe/London", "GBP");
        var from = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        PaperTradingLedgerEntry Fill(Guid worker, string symbol, decimal price,
            decimal fee, DateTimeOffset at, string direction) =>
            new(Guid.NewGuid(), worker, symbol, 1m, price, fee, at, direction);
        var report = PaperTransactionReportGenerator.Generate(profile,
            [
                Fill(first, "BTC/GBP", 100m, 0m, from.AddMinutes(-1), "buy"),
                Fill(second, "ETH/EUR", 100m, 0m, from.AddMinutes(-1), "buy"),
                Fill(first, "BTC/GBP", 120m, 1m, from.AddMinutes(30), "sell"),
                Fill(second, "ETH/EUR", 110m, 1m, from.AddMinutes(90), "sell")
            ], from, from.AddHours(3));

        Assert.Null(report.TotalInReportingCurrency);
        var format = new WebApp::Trading.Web.Reporting.GbPaperReportCountryFormatter();
        var view = format.Format(profile, report);
        Assert.Equal("GB", view.Code);
        Assert.Equal("29/03/2026 00:30:00 +00:00", view.Transactions[0].ExecutedAtLocal);
        Assert.Equal("29/03/2026 02:30:00 +01:00", view.Transactions[1].ExecutedAtLocal);
        Assert.Equal("GBP", view.Transactions[0].QuoteCurrency);
        Assert.Equal("EUR", view.Transactions[1].QuoteCurrency);
        Assert.Equal("19", view.Transactions[0].RealizedProfitAndLoss);
        Assert.Equal("9", view.Transactions[1].RealizedProfitAndLoss);
        Assert.Contains("not a UK tax return", view.Disclaimer, StringComparison.Ordinal);

        var tokyo = format.Format(new ReportingProfile("en-US", "Asia/Tokyo", "JPY"), report);
        Assert.Equal("29/03/2026 09:30:00 +09:00", tokyo.Transactions[0].ExecutedAtLocal);
        Assert.Equal("GBP", tokyo.Transactions[0].QuoteCurrency);
    }
}
