namespace Trading.Workers.Scanner;

/// <summary>
/// Explicit operator controls for informational scanner scheduling.
/// </summary>
public sealed class ScannerSchedulingOptions
{
    public static readonly TimeSpan MinimumCadence = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumCadence = TimeSpan.FromHours(24);

    /// <summary>
    /// Scanner scheduling is opt-in. Disabled is the safe default.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The bounded interval between complete scheduling passes.
    /// </summary>
    public TimeSpan Cadence { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        if (Cadence < MinimumCadence || Cadence > MaximumCadence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Cadence),
                $"Scanner cadence must be between {MinimumCadence} and {MaximumCadence}.");
        }
    }
}
