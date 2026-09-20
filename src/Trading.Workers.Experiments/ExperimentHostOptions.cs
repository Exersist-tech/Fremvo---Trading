namespace Trading.Workers.Experiments;

/// <summary>
/// Host settings for the experiment worker service. Experiments are paper-only by construction:
/// there is no setting here that can enable live or leveraged trading.
/// </summary>
public sealed class ExperimentHostOptions
{
    public const string SectionName = "Experiments";

    /// <summary>How often each user's worker pool is advanced by one slice of work.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Users whose experiment pools this host advances. Each pool is limited to ten workers by the
    /// domain model; this list bounds how many pools a single host instance drives.
    /// </summary>
    public IList<Guid> EnabledUserIds { get; } = new List<Guid>();
}
