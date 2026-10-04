using Trading.Application.Reporting;
using Trading.Domain.Experiments;
using Trading.Domain.Reporting;

namespace Trading.ArchitectureTests;

public sealed class PaperTransactionReportTests
{
    private static readonly ReportingProfile UsdProfile = new("en-US", "UTC", "USD");
    private static readonly DateTimeOffset From = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Worker = Guid.NewGuid();

    [Fact]
    public void ReplaysPriorBuysAndFeesBeforeValuingPartialSales()
    {
        var anotherWorker = Guid.NewGuid();
        var entries = new[]
        {
            Fill(Worker, "BTC/USD", 2m, 100m, 2m, From.AddMinutes(-3), "buy"),
            Fill(Worker, "BTC/USD", 2m, 120m, 2m, From.AddMinutes(-2), "buy"),
            Fill(anotherWorker, "SOL/USD", 1m, 50m, 0m, From.AddMinutes(-1), "buy"),
            Fill(Worker, "BTC/USD", 1m, 130m, 1m, From, "SELL"),
            Fill(anotherWorker, "SOL/USD", 1m, 60m, 1m, From.AddMinutes(1), "sell"),
            Fill(Worker, "BTC/USD", 1m, 140m, 1m, From.AddMinutes(2), "sell"),
            Fill(Worker, "BTC/USD", 2m, 100m, 0m, From.AddMinutes(3), "sell")
        };

        var report = PaperTransactionReportGenerator.Generate(UsdProfile, entries,
            From, From.AddMinutes(3));

        Assert.Equal(3, report.Transactions.Count);
        Assert.Equal(new decimal?[] { 18m, 9m, 28m },
            report.Transactions.Select(row => row.RealizedProfitAndLoss).ToArray());
        Assert.Equal(55m, report.CurrencyTotals.Single().RealizedProfitAndLoss);
        Assert.Equal(55m, report.TotalInReportingCurrency);
        Assert.Equal(129m, report.Transactions[0].CashChange);
        Assert.Equal("sell", report.Transactions[0].Direction);
        Assert.All(report.Transactions, row => Assert.Equal("USD", row.QuoteCurrency));
        Assert.Contains("not tax, legal, or financial advice", report.Disclaimer, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedQuotesAreNeverMergedOrConvertedToReportingCurrency()
    {
        var eurWorker = Guid.NewGuid();
        var report = PaperTransactionReportGenerator.Generate(UsdProfile,
            [
                Fill(Worker, "BTC/USD", 1m, 100m, 0m, From, "buy"),
                Fill(Worker, "BTC/USD", 1m, 110m, 1m, From.AddMinutes(1), "sell"),
                Fill(eurWorker, "ETH/EUR", 1m, 50m, 0m, From, "buy"),
                Fill(eurWorker, "ETH/EUR", 1m, 55m, 1m, From.AddMinutes(1), "sell")
            ], From, From.AddMinutes(2));

        Assert.Null(report.TotalInReportingCurrency);
        Assert.Equal(4m, report.CurrencyTotals.Single(total => total.Currency == "EUR").RealizedProfitAndLoss);
        Assert.Equal(9m, report.CurrencyTotals.Single(total => total.Currency == "USD").RealizedProfitAndLoss);
    }

    [Fact]
    public void ASecondLocaleZoneAndCurrencyDoNotShiftUtcReportBoundaries()
    {
        var profile = new ReportingProfile("fr-FR", "Asia/Tokyo", "EUR");
        var report = PaperTransactionReportGenerator.Generate(profile,
            [
                Fill(Worker, "ETH/EUR", 1m, 50m, 0m, From.AddMinutes(-1), "buy"),
                Fill(Worker, "ETH/EUR", 1m, 55m, 1m, From, "sell"),
                Fill(Worker, "ETH/EUR", 1m, 60m, 0m, From.AddMinutes(1), "buy")
            ], From, From.AddMinutes(1));
        Assert.Equal("EUR", report.ReportingCurrency);
        Assert.Equal(4m, report.TotalInReportingCurrency);
        Assert.Equal(From, Assert.Single(report.Transactions).ExecutedAtUtc);
        Assert.Equal(From.AddMinutes(1), report.ToUtcExclusive);
    }

    [Fact]
    public void InvalidOrDuplicateLedgerEvidenceFailsRatherThanReturningProfit()
    {
        var first = Fill(Worker, "BTC/USD", 1m, 100m, 0m, From, "buy");
        var sell = Fill(Worker, "BTC/USD", 2m, 110m, 0m, From.AddMinutes(1), "sell");
        Assert.Throws<InvalidOperationException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile, [first, first], From, From.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile,
                [first, Fill(Worker, "BTC/USD", 1m, 110m, 0m, From, "sell")],
                From, From.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile, [first, sell], From, From.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile,
                [Fill(Worker, "BTCUSD", 1m, 100m, 0m, From, "buy")], From, From.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile,
                [Fill(Worker, "BTC/USD", 1m, 100m, 0m, From, "open")], From, From.AddHours(1)));
        Assert.Throws<InvalidOperationException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile,
                [Fill(Worker, "BTC/USD", 1m, 100m, 0m, From.ToOffset(TimeSpan.FromHours(1)), "buy")],
                From, From.AddHours(1)));
        Assert.Throws<ArgumentException>(() =>
            PaperTransactionReportGenerator.Generate(UsdProfile, [first],
                From.ToOffset(TimeSpan.FromHours(1)), From.AddHours(1)));
    }

    private static PaperTradingLedgerEntry Fill(Guid worker, string symbol,
        decimal quantity, decimal price, decimal fee, DateTimeOffset when, string direction) =>
        new(Guid.NewGuid(), worker, symbol, quantity, price, fee, when, direction);
}
