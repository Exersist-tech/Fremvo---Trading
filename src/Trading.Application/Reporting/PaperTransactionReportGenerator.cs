using Trading.Domain.Experiments;
using Trading.Domain.Reporting;

namespace Trading.Application.Reporting;

public sealed record PaperTransactionReportRow(
    Guid WorkerId,
    Guid FillId,
    DateTimeOffset ExecutedAtUtc,
    string Symbol,
    string QuoteCurrency,
    string Direction,
    decimal Quantity,
    decimal Price,
    decimal Fee,
    decimal GrossValue,
    decimal CashChange,
    decimal? RealizedProfitAndLoss);

public sealed record PaperTransactionCurrencyTotal(string Currency, decimal RealizedProfitAndLoss);

public sealed record PaperTransactionReport(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtcExclusive,
    string ReportingCurrency,
    IReadOnlyList<PaperTransactionReportRow> Transactions,
    IReadOnlyList<PaperTransactionCurrencyTotal> CurrencyTotals,
    decimal? TotalInReportingCurrency,
    string Disclaimer)
{
    public const string RequiredDisclaimer =
        "Simulated paper transactions only. Native quote currencies are not converted. " +
        "This report is informational, not tax, legal, or financial advice; past results do not predict future results.";
}

public static class PaperTransactionReportGenerator
{
    public static PaperTransactionReport Generate(
        ReportingProfile profile,
        IEnumerable<PaperTradingLedgerEntry> ownerEntries,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtcExclusive)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(ownerEntries);
        if (fromUtc.Offset != TimeSpan.Zero || toUtcExclusive.Offset != TimeSpan.Zero
            || fromUtc >= toUtcExclusive)
            throw new ArgumentException("A nonempty UTC report interval is required.");

        var entries = ownerEntries.OrderBy(entry => entry.OccurredAtUtc).ThenBy(entry => entry.Id).ToArray();
        var usedIds = new HashSet<Guid>();
        var usedWorkerTimes = new HashSet<(Guid WorkerId, DateTimeOffset OccurredAtUtc)>();
        var portfolios = new Dictionary<Guid, (decimal Quantity, decimal AverageCost, string Symbol)>();
        var transactions = new List<PaperTransactionReportRow>();
        foreach (var entry in entries)
        {
            if (entry.OccurredAtUtc >= toUtcExclusive)
                continue;
            if (entry.Id == Guid.Empty || entry.WorkerId == Guid.Empty || !usedIds.Add(entry.Id)
                || !usedWorkerTimes.Add((entry.WorkerId, entry.OccurredAtUtc))
                || entry.OccurredAtUtc.Offset != TimeSpan.Zero
                || entry.Quantity <= 0m || entry.ExecutionPrice <= 0m || entry.Fee < 0m)
                throw new InvalidOperationException("Paper ledger contains incomplete or duplicate transaction evidence.");

            var parts = entry.Symbol.Split('/');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                throw new InvalidOperationException("A paper Spot transaction requires an explicit base/quote pair.");
            var quote = parts[1];
            var state = portfolios.GetValueOrDefault(entry.WorkerId);
            if (state.Symbol is not null && !state.Symbol.Equals(entry.Symbol, StringComparison.Ordinal))
                throw new InvalidOperationException("A worker's paper ledger contains multiple instruments.");

            var gross = checked(entry.Quantity * entry.ExecutionPrice);
            decimal? realized = null;
            decimal cashChange;
            string direction;
            if (entry.Direction.Equals("buy", StringComparison.OrdinalIgnoreCase))
            {
                direction = "buy";
                cashChange = checked(-gross - entry.Fee);
                var nextQuantity = checked(state.Quantity + entry.Quantity);
                var averageCost = checked((state.Quantity * state.AverageCost + gross + entry.Fee) / nextQuantity);
                state = (nextQuantity, averageCost, entry.Symbol);
            }
            else if (entry.Direction.Equals("sell", StringComparison.OrdinalIgnoreCase))
            {
                direction = "sell";
                if (entry.Quantity > state.Quantity)
                    throw new InvalidOperationException("A paper sale exceeds the worker's recorded position.");
                cashChange = checked(gross - entry.Fee);
                realized = checked((entry.ExecutionPrice - state.AverageCost) * entry.Quantity - entry.Fee);
                var remaining = state.Quantity - entry.Quantity;
                state = (remaining, remaining == 0m ? 0m : state.AverageCost, entry.Symbol);
            }
            else
                throw new InvalidOperationException("Paper ledger contains an unknown trade direction.");

            portfolios[entry.WorkerId] = state;
            if (entry.OccurredAtUtc >= fromUtc)
                transactions.Add(new PaperTransactionReportRow(
                    entry.WorkerId, entry.Id, entry.OccurredAtUtc, entry.Symbol, quote,
                    direction, entry.Quantity, entry.ExecutionPrice,
                    entry.Fee, gross, cashChange, realized));
        }

        var totals = transactions.GroupBy(row => row.QuoteCurrency, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new PaperTransactionCurrencyTotal(
                group.Key, group.Sum(row => row.RealizedProfitAndLoss ?? 0m)))
            .ToArray();
        var total = totals.All(item => item.Currency == profile.ReportingCurrency)
            ? totals.Sum(item => item.RealizedProfitAndLoss) : (decimal?)null;
        return new PaperTransactionReport(fromUtc, toUtcExclusive, profile.ReportingCurrency,
            transactions, totals, total, PaperTransactionReport.RequiredDisclaimer);
    }
}
