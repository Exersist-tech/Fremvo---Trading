using Trading.Domain.Identity;
using Trading.Domain.Reporting;
using Trading.Domain.Users;

namespace Trading.ArchitectureTests;

public sealed class ReportingProfileTests
{
    [Fact]
    public void OwnerCanChangeLocaleIanaZoneAndIsoCurrencyWithoutChangingIdentity()
    {
        Assert.Equal("UTC", new ReportingProfile("en-US", "UTC", "USD").TimeZone);
        var id = Guid.NewGuid();
        var user = new User(id, "owner@example.test", "Owner", "en-US", "UTC", "USD",
            RoleType.User, false, UserStatus.Active);
        var profile = new ReportingProfile("fr-FR", "Asia/Tokyo", "EUR");

        user.ChangeReportingProfile(profile);

        Assert.Equal("fr-FR", user.Locale);
        Assert.Equal("Asia/Tokyo", user.TimeZone);
        Assert.Equal("EUR", user.ReportingCurrency);
        Assert.Equal(id, user.Id);
        Assert.Equal(RoleType.User, user.Role);
    }

    [Theory]
    [InlineData("not-a-real-culture", "Asia/Tokyo", "EUR")]
    [InlineData("fr-FR", "Unknown/Location", "EUR")]
    [InlineData("fr-FR", "Asia/Tokyo", "ZZZ")]
    [InlineData("fr-FR", "Asia/Tokyo", "eur")]
    public void InvalidReportingPreferencesAreNotAccepted(string locale, string zone, string currency)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ReportingProfile(locale, zone, currency));
    }
}
