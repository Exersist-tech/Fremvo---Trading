using Microsoft.EntityFrameworkCore;
using Trading.Infrastructure.Data;

var targetConnection = Environment.GetEnvironmentVariable("TRADING_SCHEMA_TARGET");
var referenceConnection = Environment.GetEnvironmentVariable("TRADING_SCHEMA_REFERENCE");
if (string.IsNullOrWhiteSpace(targetConnection) || string.IsNullOrWhiteSpace(referenceConnection))
{
    await Console.Error.WriteLineAsync(
        "Set TRADING_SCHEMA_TARGET and TRADING_SCHEMA_REFERENCE to distinct SQL connection strings.").ConfigureAwait(false);
    return 2;
}

try
{
    var target = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
        .UseSqlServer(targetConnection).Options);
    await using var targetLifetime = target.ConfigureAwait(false);
    var reference = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
        .UseSqlServer(referenceConnection).Options);
    await using var referenceLifetime = reference.ConfigureAwait(false);
    var comparison = await TradingSqlSchemaComparer.CompareAsync(target, reference).ConfigureAwait(false);
    if (!comparison.Matches)
    {
        foreach (var missing in comparison.MissingFromTarget.Take(50))
            await Console.Out.WriteLineAsync($"Missing from target: {missing}").ConfigureAwait(false);
        foreach (var unexpected in comparison.UnexpectedInTarget.Take(50))
            await Console.Out.WriteLineAsync($"Unexpected in target: {unexpected}").ConfigureAwait(false);
        await Console.Error.WriteLineAsync("Schema differs; do not adopt migration history.").ConfigureAwait(false);
        return 1;
    }
    await Console.Out.WriteLineAsync(
        "Schema matches the migrated reference catalog. Read-only check only: no migration history was changed.").ConfigureAwait(false);
    return 0;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    await Console.Error.WriteLineAsync(
        $"Schema verification failed ({exception.GetType().Name}); do not adopt migration history.").ConfigureAwait(false);
    return 1;
}
