namespace Trading.Web.Optimization;

using Trading.Optimization;
using Trading.Strategies;

/// <summary>
/// Browser-facing payload for validating an optimization split plan.
/// Contains no secrets and no exchange credentials.
/// </summary>
internal sealed record OptimizationPlanDto(
    string Symbol,
    DateTimeOffset TrainingFromUtc,
    DateTimeOffset TrainingToUtc,
    DateTimeOffset ValidationFromUtc,
    DateTimeOffset ValidationToUtc,
    DateTimeOffset HoldoutFromUtc,
    DateTimeOffset HoldoutToUtc,
    IReadOnlyList<OptimizationParameterDto>? Parameters);

internal sealed record OptimizationParameterDto(
    string Name,
    decimal Minimum,
    decimal Maximum,
    decimal DefaultValue,
    string? Description);

internal static class OptimizationPlanMapping
{
    public static OptimizationPlanRequest ToRequest(this OptimizationPlanDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var definitions = (dto.Parameters ?? Array.Empty<OptimizationParameterDto>())
            .Select(p => new StrategyParameterDefinition(
                p.Name,
                p.Minimum,
                p.Maximum,
                p.DefaultValue,
                p.Description ?? string.Empty))
            .ToList();

        return new OptimizationPlanRequest(
            dto.Symbol,
            dto.TrainingFromUtc.ToUniversalTime(),
            dto.TrainingToUtc.ToUniversalTime(),
            dto.ValidationFromUtc.ToUniversalTime(),
            dto.ValidationToUtc.ToUniversalTime(),
            dto.HoldoutFromUtc.ToUniversalTime(),
            dto.HoldoutToUtc.ToUniversalTime(),
            definitions);
    }
}
