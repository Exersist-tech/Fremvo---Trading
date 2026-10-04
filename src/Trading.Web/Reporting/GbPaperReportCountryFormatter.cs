using System.Globalization;
using Trading.Application.Reporting;
using Trading.Domain.Reporting;

namespace Trading.Web.Reporting;

public sealed class GbPaperReportCountryFormatter : IPaperReportCountryFormatter
{
    private static readonly CultureInfo s_culture = CultureInfo.GetCultureInfo("en-GB");

    public string Code => "GB";

    public PaperCountryReport Format(ReportingProfile profile, PaperTransactionReport report)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(report);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(profile.TimeZone);
        return new PaperCountryReport(Code, "United Kingdom date-notation example", profile.TimeZone,
            "Presentation example only. Native quote currencies are not converted; " +
            "this is not a UK tax return or tax, legal, or financial advice.",
            report.Transactions.Select(row => new PaperCountryReportRow(
                TimeZoneInfo.ConvertTime(row.ExecutedAtUtc, zone)
                    .ToString("dd/MM/yyyy HH:mm:ss zzz", s_culture),
                row.WorkerId.ToString("D"), row.FillId.ToString("D"),
                row.Symbol, row.Direction, row.QuoteCurrency,
                Exact(row.Quantity), Exact(row.Price), Exact(row.Fee),
                Exact(row.CashChange),
                row.RealizedProfitAndLoss is decimal realized ? Exact(realized) : null))
                .ToArray());
    }

    private static string Exact(decimal amount) => amount.ToString("G29", CultureInfo.InvariantCulture);
}
