using Trading.Domain.Reporting;

namespace Trading.Application.Reporting;

public sealed record PaperCountryReportRow(
    string ExecutedAtLocal, string WorkerId, string FillId, string Symbol,
    string Direction, string QuoteCurrency, string Quantity, string Price,
    string Fee, string CashChange, string? RealizedProfitAndLoss);

public sealed record PaperCountryReport(
    string Code, string Title, string TimeZone, string Disclaimer,
    IReadOnlyList<PaperCountryReportRow> Transactions);

public interface IPaperReportCountryFormatter
{
    string Code { get; }
    PaperCountryReport Format(ReportingProfile profile, PaperTransactionReport report);
}
