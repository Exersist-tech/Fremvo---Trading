using System.Collections.ObjectModel;

namespace Trading.Domain.Sessions;

public enum SessionCrossMidnightBehavior
{
    SameLocalDay = 0,
    EndOnFollowingLocalDay = 1
}

public enum SessionInvalidLocalTimePolicy
{
    RejectOccurrence = 0,
    ShiftForwardToFirstValidTime = 1
}

public enum SessionAmbiguousLocalTimePolicy
{
    PreferEarlierUtcInstant = 0,
    PreferLaterUtcInstant = 1
}

/// <summary>
/// Defines how a wall-clock session is resolved at daylight-saving transitions.
/// Invalid local boundaries are either rejected or moved to the first valid wall-clock time.
/// Ambiguous local boundaries select the earlier or later UTC occurrence explicitly.
/// </summary>
public sealed record SessionDstPolicy
{
    public SessionDstPolicy(
        SessionInvalidLocalTimePolicy invalidLocalTimePolicy,
        SessionAmbiguousLocalTimePolicy ambiguousLocalTimePolicy)
    {
        if (!Enum.IsDefined(invalidLocalTimePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(invalidLocalTimePolicy));
        }

        if (!Enum.IsDefined(ambiguousLocalTimePolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(ambiguousLocalTimePolicy));
        }

        InvalidLocalTimePolicy = invalidLocalTimePolicy;
        AmbiguousLocalTimePolicy = ambiguousLocalTimePolicy;
    }

    public static SessionDstPolicy FailClosed { get; } = new(
        SessionInvalidLocalTimePolicy.RejectOccurrence,
        SessionAmbiguousLocalTimePolicy.PreferEarlierUtcInstant);

    public SessionInvalidLocalTimePolicy InvalidLocalTimePolicy { get; }

    public SessionAmbiguousLocalTimePolicy AmbiguousLocalTimePolicy { get; }
}

public sealed record SessionProfileVersionIdentity
{
    public SessionProfileVersionIdentity(string profileId, int version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Session profile version must be positive.");
        }

        ProfileId = profileId.Trim();
        Version = version;
    }

    public string ProfileId { get; }

    public int Version { get; }
}

/// <summary>
/// An immutable platform-owned, versioned session definition. The IANA ID is retained as supplied;
/// Windows lookup is performed only through the .NET IANA-to-Windows mapping and never falls back
/// to the machine's local zone or UTC.
/// </summary>
public sealed class SessionProfile
{
    public SessionProfile(
        SessionProfileVersionIdentity identity,
        string name,
        string ianaTimeZoneId,
        TimeOnly localStartTime,
        TimeOnly localEndTime,
        IEnumerable<DayOfWeek> allowedStartDays,
        SessionCrossMidnightBehavior crossMidnightBehavior,
        SessionDstPolicy dstPolicy)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(ianaTimeZoneId);
        ArgumentNullException.ThrowIfNull(allowedStartDays);
        ArgumentNullException.ThrowIfNull(dstPolicy);

        if (!Enum.IsDefined(crossMidnightBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(crossMidnightBehavior));
        }

        ValidateAndResolveIanaTimeZone(ianaTimeZoneId);

        var days = allowedStartDays.Distinct().OrderBy(day => day).ToArray();
        if (days.Length == 0 || days.Any(day => !Enum.IsDefined(day)))
        {
            throw new ArgumentException("At least one valid allowed start day is required.", nameof(allowedStartDays));
        }

        if (crossMidnightBehavior == SessionCrossMidnightBehavior.SameLocalDay && localStartTime >= localEndTime)
        {
            throw new ArgumentException("A same-day session must end after it starts.", nameof(localEndTime));
        }

        if (crossMidnightBehavior == SessionCrossMidnightBehavior.EndOnFollowingLocalDay && localStartTime <= localEndTime)
        {
            throw new ArgumentException("A cross-midnight session must end before it starts.", nameof(localEndTime));
        }

        Identity = identity;
        Name = name.Trim();
        IanaTimeZoneId = ianaTimeZoneId;
        LocalStartTime = localStartTime;
        LocalEndTime = localEndTime;
        AllowedStartDays = Array.AsReadOnly(days);
        CrossMidnightBehavior = crossMidnightBehavior;
        DstPolicy = dstPolicy;
    }

    public SessionProfileVersionIdentity Identity { get; }

    public string Name { get; }

    public string IanaTimeZoneId { get; }

    public TimeOnly LocalStartTime { get; }

    public TimeOnly LocalEndTime { get; }

    public ReadOnlyCollection<DayOfWeek> AllowedStartDays { get; }

    public SessionCrossMidnightBehavior CrossMidnightBehavior { get; }

    public SessionDstPolicy DstPolicy { get; }

    internal static TimeZoneInfo ResolveTimeZone(string ianaTimeZoneId)
    {
        ValidateAndResolveIanaTimeZone(ianaTimeZoneId, out var timeZone);
        return timeZone;
    }

    private static void ValidateAndResolveIanaTimeZone(string ianaTimeZoneId)
    {
        ValidateAndResolveIanaTimeZone(ianaTimeZoneId, out _);
    }

    private static void ValidateAndResolveIanaTimeZone(string ianaTimeZoneId, out TimeZoneInfo timeZone)
    {
        if (!string.Equals(ianaTimeZoneId, ianaTimeZoneId.Trim(), StringComparison.Ordinal)
            || !TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaTimeZoneId, out var windowsTimeZoneId)
            || !TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsTimeZoneId, out _))
        {
            throw new ArgumentException("A canonical, supported IANA time-zone ID is required.", nameof(ianaTimeZoneId));
        }

        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(windowsTimeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException("The mapped IANA time zone is unavailable on this platform.", nameof(ianaTimeZoneId), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException("The mapped IANA time zone is invalid on this platform.", nameof(ianaTimeZoneId), exception);
        }
    }
}

public sealed record SessionBoundaryEvidence(
    DateTime RequestedLocalTime,
    DateTime EffectiveLocalTime,
    DateTimeOffset? UtcInstant,
    string Resolution);

public sealed record SessionMembershipEvidence(
    SessionProfileVersionIdentity ProfileIdentity,
    string IanaTimeZoneId,
    DateTimeOffset EvaluatedUtcInstant,
    DateTimeOffset LocalInstant,
    DateOnly? SessionStartDate,
    SessionBoundaryEvidence? StartBoundary,
    SessionBoundaryEvidence? EndBoundary,
    string Rationale);

public sealed record SessionMembershipResult(bool IsMember, SessionMembershipEvidence Evidence);

/// <summary>
/// Evaluates a supplied UTC instant against a session profile. Membership is start-inclusive and
/// end-exclusive. For cross-midnight sessions allowed days apply to the day on which the session starts.
/// </summary>
public static class SessionMembershipEngine
{
    public static SessionMembershipResult Evaluate(SessionProfile profile, DateTimeOffset utcInstant)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (utcInstant.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("The evaluated instant must be expressed in UTC.", nameof(utcInstant));
        }

        var zone = SessionProfile.ResolveTimeZone(profile.IanaTimeZoneId);
        var localInstant = TimeZoneInfo.ConvertTime(utcInstant, zone);
        var candidateDates = profile.CrossMidnightBehavior == SessionCrossMidnightBehavior.EndOnFollowingLocalDay
            ? new[] { DateOnly.FromDateTime(localInstant.Date), DateOnly.FromDateTime(localInstant.Date).AddDays(-1) }
            : new[] { DateOnly.FromDateTime(localInstant.Date) };

        SessionMembershipEvidence? unavailableEvidence = null;
        foreach (var candidateDate in candidateDates)
        {
            if (!profile.AllowedStartDays.Contains(candidateDate.DayOfWeek))
            {
                continue;
            }

            var occurrence = ResolveOccurrence(profile, zone, candidateDate);
            if (!occurrence.IsAvailable)
            {
                unavailableEvidence ??= CreateEvidence(profile, utcInstant, localInstant, candidateDate, occurrence, occurrence.Rationale);
                continue;
            }

            var isMember = utcInstant >= occurrence.Start!.UtcInstant && utcInstant < occurrence.End!.UtcInstant;
            var rationale = isMember
                ? "The UTC instant is within the start-inclusive, end-exclusive resolved session interval."
                : "The UTC instant is outside the resolved session interval.";
            return new SessionMembershipResult(
                isMember,
                CreateEvidence(profile, utcInstant, localInstant, candidateDate, occurrence, rationale));
        }

        return new SessionMembershipResult(
            false,
            unavailableEvidence ?? new SessionMembershipEvidence(
                profile.Identity,
                profile.IanaTimeZoneId,
                utcInstant,
                localInstant,
                null,
                null,
                null,
                "No session occurrence is allowed for the local start day."));
    }

    private static ResolvedOccurrence ResolveOccurrence(SessionProfile profile, TimeZoneInfo zone, DateOnly startDate)
    {
        var start = ResolveBoundary(profile, zone, startDate, profile.LocalStartTime);
        var endDate = profile.CrossMidnightBehavior == SessionCrossMidnightBehavior.EndOnFollowingLocalDay
            ? startDate.AddDays(1)
            : startDate;
        var end = ResolveBoundary(profile, zone, endDate, profile.LocalEndTime);

        if (start.UtcInstant is null || end.UtcInstant is null)
        {
            return new ResolvedOccurrence(start, end, "The session occurrence is unavailable under its DST policy.");
        }

        if (end.UtcInstant <= start.UtcInstant)
        {
            return new ResolvedOccurrence(start, end, "The resolved session boundaries do not form a positive interval.");
        }

        return new ResolvedOccurrence(start, end, null);
    }

    private static ResolvedBoundary ResolveBoundary(SessionProfile profile, TimeZoneInfo zone, DateOnly date, TimeOnly time)
    {
        var requested = date.ToDateTime(time, DateTimeKind.Unspecified);
        var effective = requested;
        var resolution = "Exact local time.";

        if (zone.IsInvalidTime(requested))
        {
            if (profile.DstPolicy.InvalidLocalTimePolicy == SessionInvalidLocalTimePolicy.RejectOccurrence)
            {
                return new ResolvedBoundary(requested, requested, null, "Invalid local time rejected by DST policy.");
            }

            var limit = requested.AddDays(1);
            while (zone.IsInvalidTime(effective) && effective < limit)
            {
                effective = effective.AddMinutes(1);
            }

            if (zone.IsInvalidTime(effective))
            {
                return new ResolvedBoundary(requested, effective, null, "No valid local time was found within one day.");
            }

            resolution = "Invalid local time shifted forward to the first valid local minute.";
        }

        if (zone.IsAmbiguousTime(effective))
        {
            var candidates = zone.GetAmbiguousTimeOffsets(effective)
                .Select(offset => new DateTimeOffset(effective, offset).ToUniversalTime())
                .OrderBy(instant => instant)
                .ToArray();
            var selected = profile.DstPolicy.AmbiguousLocalTimePolicy == SessionAmbiguousLocalTimePolicy.PreferEarlierUtcInstant
                ? candidates[0]
                : candidates[^1];
            return new ResolvedBoundary(requested, effective, selected, $"{resolution} Ambiguous local time resolved to the {profile.DstPolicy.AmbiguousLocalTimePolicy} occurrence.");
        }

        return new ResolvedBoundary(
            requested,
            effective,
            TimeZoneInfo.ConvertTimeToUtc(effective, zone),
            resolution);
    }

    private static SessionMembershipEvidence CreateEvidence(
        SessionProfile profile,
        DateTimeOffset utcInstant,
        DateTimeOffset localInstant,
        DateOnly sessionStartDate,
        ResolvedOccurrence occurrence,
        string rationale) =>
        new(
            profile.Identity,
            profile.IanaTimeZoneId,
            utcInstant,
            localInstant,
            sessionStartDate,
            occurrence.Start?.ToEvidence(),
            occurrence.End?.ToEvidence(),
            rationale);

    private sealed record ResolvedOccurrence(ResolvedBoundary? Start, ResolvedBoundary? End, string? UnavailableReason)
    {
        public bool IsAvailable => UnavailableReason is null;

        public string Rationale => UnavailableReason ?? string.Empty;
    }

    private sealed record ResolvedBoundary(
        DateTime RequestedLocalTime,
        DateTime EffectiveLocalTime,
        DateTimeOffset? UtcInstant,
        string Resolution)
    {
        public SessionBoundaryEvidence ToEvidence() =>
            new(RequestedLocalTime, EffectiveLocalTime, UtcInstant, Resolution);
    }
}
