using System.Collections.Frozen;
using System.Globalization;

namespace Trading.Domain.Reporting;

public sealed class ReportingProfile
{
    private static readonly FrozenSet<string> s_currencies = CultureInfo
        .GetCultures(CultureTypes.SpecificCultures)
        .Select(culture => new RegionInfo(culture.Name).ISOCurrencySymbol)
        .Where(symbol => symbol.Length == 3 && symbol.All(char.IsAsciiLetterUpper))
        .ToFrozenSet(StringComparer.Ordinal);

    public ReportingProfile(string locale, string timeZone, string reportingCurrency)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Length > 16)
            throw new ArgumentException("A supported locale is required.", nameof(locale));
        if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 64
            || timeZone != "UTC" && !timeZone.Contains('/', StringComparison.Ordinal)
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _))
            throw new ArgumentException("A supported IANA time zone is required.", nameof(timeZone));
        if (string.IsNullOrWhiteSpace(reportingCurrency) || !s_currencies.Contains(reportingCurrency))
            throw new ArgumentException("A supported three-letter ISO reporting currency is required.", nameof(reportingCurrency));

        Locale = CultureInfo.GetCultureInfo(locale).Name;
        TimeZone = timeZone;
        ReportingCurrency = reportingCurrency;
    }

    public string Locale { get; }
    public string TimeZone { get; }
    public string ReportingCurrency { get; }
}
