using System.Globalization;
using System.Reflection;
using Trading.Domain.Execution;
using Trading.Domain.Sessions;

namespace Trading.ArchitectureTests;

public sealed class SessionProfileTests
{
    [Fact]
    public void AcceptsMappedCanonicalIanaZoneAndRejectsWindowsUnknownAndWhitespaceIds()
    {
        var profile = Profile("America/New_York", new TimeOnly(9, 0), new TimeOnly(10, 0));

        Assert.Equal("America/New_York", profile.IanaTimeZoneId);
        Assert.Throws<ArgumentException>(() => Profile("Eastern Standard Time", new TimeOnly(9, 0), new TimeOnly(10, 0)));
        Assert.Throws<ArgumentException>(() => Profile("Not/AZone", new TimeOnly(9, 0), new TimeOnly(10, 0)));
        Assert.Throws<ArgumentException>(() => Profile(" America/New_York", new TimeOnly(9, 0), new TimeOnly(10, 0)));
    }

    [Fact]
    public void ConvertsUtcToIanaLocalTimeAndUsesInclusiveStartExclusiveEnd()
    {
        var profile = Profile("America/New_York", new TimeOnly(9, 0), new TimeOnly(10, 0));

        var start = SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 5, 14));
        var within = SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 5, 14, 30));
        var end = SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 5, 15));

        Assert.True(start.IsMember);
        Assert.True(within.IsMember);
        Assert.False(end.IsMember);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.FromHours(-5)), start.Evidence.LocalInstant);
        Assert.Equal("America/New_York", start.Evidence.IanaTimeZoneId);
    }

    [Fact]
    public void AppliesAllowedDaysToTheStartOfAnExplicitCrossMidnightSession()
    {
        var profile = Profile(
            "Etc/UTC",
            new TimeOnly(22, 0),
            new TimeOnly(2, 0),
            [DayOfWeek.Monday],
            SessionCrossMidnightBehavior.EndOnFollowingLocalDay);

        Assert.True(SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 5, 22)).IsMember);
        Assert.True(SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 6, 1, 59)).IsMember);
        Assert.False(SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 6, 2)).IsMember);
        Assert.False(SessionMembershipEngine.Evaluate(profile, Utc(2026, 1, 6, 22)).IsMember);
    }

    [Fact]
    public void RejectsSpringGapOccurrenceWhenConfiguredFailClosed()
    {
        var profile = Profile(
            "America/New_York",
            new TimeOnly(2, 30),
            new TimeOnly(4, 0),
            dstPolicy: SessionDstPolicy.FailClosed);

        var result = SessionMembershipEngine.Evaluate(profile, Utc(2026, 3, 8, 7, 30));

        Assert.False(result.IsMember);
        Assert.Contains("DST policy", result.Evidence.Rationale, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Evidence.StartBoundary);
        Assert.Null(result.Evidence.StartBoundary!.UtcInstant);
    }

    [Fact]
    public void ShiftsSpringGapBoundaryToFirstValidLocalMinuteWhenConfigured()
    {
        var profile = Profile(
            "America/New_York",
            new TimeOnly(2, 30),
            new TimeOnly(4, 0),
            dstPolicy: new SessionDstPolicy(
                SessionInvalidLocalTimePolicy.ShiftForwardToFirstValidTime,
                SessionAmbiguousLocalTimePolicy.PreferEarlierUtcInstant));

        var result = SessionMembershipEngine.Evaluate(profile, Utc(2026, 3, 8, 7));

        Assert.True(result.IsMember);
        Assert.Equal(new DateTime(2026, 3, 8, 3, 0, 0), result.Evidence.StartBoundary!.EffectiveLocalTime);
    }

    [Fact]
    public void ResolvesBothFallBackOccurrencesAccordingToExplicitPolicy()
    {
        var earlier = Profile(
            "America/New_York",
            new TimeOnly(1, 30),
            new TimeOnly(2, 30),
            dstPolicy: new SessionDstPolicy(
                SessionInvalidLocalTimePolicy.RejectOccurrence,
                SessionAmbiguousLocalTimePolicy.PreferEarlierUtcInstant));
        var later = Profile(
            "America/New_York",
            new TimeOnly(1, 30),
            new TimeOnly(2, 30),
            dstPolicy: new SessionDstPolicy(
                SessionInvalidLocalTimePolicy.RejectOccurrence,
                SessionAmbiguousLocalTimePolicy.PreferLaterUtcInstant));

        Assert.True(SessionMembershipEngine.Evaluate(earlier, Utc(2026, 11, 1, 5, 45)).IsMember);
        Assert.False(SessionMembershipEngine.Evaluate(later, Utc(2026, 11, 1, 5, 45)).IsMember);
        Assert.True(SessionMembershipEngine.Evaluate(later, Utc(2026, 11, 1, 6, 45)).IsMember);
    }

    [Fact]
    public void IsImmutableVersionedDeterministicCultureIndependentAndHasNoExecutionSurface()
    {
        var profile = Profile("Etc/UTC", new TimeOnly(9, 0), new TimeOnly(10, 0), [DayOfWeek.Monday]);
        var input = Utc(2026, 1, 5, 9, 30);
        var originalCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var first = SessionMembershipEngine.Evaluate(profile, input);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var second = SessionMembershipEngine.Evaluate(profile, input);

            Assert.Equal(first, second);
            Assert.Equal(new SessionProfileVersionIdentity("platform.london", 1), profile.Identity);
            Assert.Throws<NotSupportedException>(() => ((IList<DayOfWeek>)profile.AllowedStartDays).Add(DayOfWeek.Tuesday));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        Assert.Throws<ArgumentException>(() => SessionMembershipEngine.Evaluate(profile, new DateTimeOffset(2026, 1, 5, 9, 30, 0, TimeSpan.FromHours(1))));
        Assert.DoesNotContain(
            typeof(SessionMembershipEngine).GetMembers(BindingFlags.Public | BindingFlags.Static),
            member => member switch
            {
                PropertyInfo property => typeof(TradeIntent).IsAssignableFrom(property.PropertyType),
                MethodInfo method => typeof(TradeIntent).IsAssignableFrom(method.ReturnType)
                    || method.GetParameters().Any(parameter => typeof(TradeIntent).IsAssignableFrom(parameter.ParameterType)),
                _ => false
            });
    }

    private static SessionProfile Profile(
        string zone,
        TimeOnly start,
        TimeOnly end,
        IReadOnlyCollection<DayOfWeek>? days = null,
        SessionCrossMidnightBehavior behavior = SessionCrossMidnightBehavior.SameLocalDay,
        SessionDstPolicy? dstPolicy = null) =>
        new(
            new SessionProfileVersionIdentity("platform.london", 1),
            "Platform session",
            zone,
            start,
            end,
            days ?? [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday],
            behavior,
            dstPolicy ?? SessionDstPolicy.FailClosed);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
