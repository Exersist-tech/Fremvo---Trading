using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Trading.Infrastructure.Data;

/// <summary>Waits for the web/bootstrap or deployment migration to finish before worker loops start.</summary>
public sealed class PaperHostSchemaReadiness : IHostedService
{
    private static readonly TimeSpan s_retryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromMinutes(2);
    private static readonly Action<ILogger, Exception?> s_ready =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, "PaperHostSchemaReady"),
            "Paper host database schema is ready; worker loops may start.");
    private static readonly Action<ILogger, int, Exception?> s_waitingForDatabase =
        LoggerMessage.Define<int>(LogLevel.Warning, new EventId(2, "PaperHostDatabaseUnavailable"),
            "Paper host is waiting for its configured SQL database (error {ErrorNumber}).");
    private static readonly Action<ILogger, Exception?> s_waitingForSchema =
        LoggerMessage.Define(LogLevel.Warning, new EventId(3, "PaperHostSchemaUnavailable"),
            "Paper host is waiting for the configured SQL schema; it will not create or repair tables.");
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<PaperHostSchemaReadiness> _logger;
    private readonly TimeSpan _timeout;

    public PaperHostSchemaReadiness(
        IServiceScopeFactory scopes,
        TimeProvider time,
        ILogger<PaperHostSchemaReadiness> logger,
        TimeSpan? timeout = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout ?? s_defaultTimeout;
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + _timeout;
        var reported = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                if (await HasSchemaAsync(db, cancellationToken).ConfigureAwait(false))
                {
                    s_ready(_logger, null);
                    return;
                }
            }
            catch (SqlException exception) when (exception.Number is 4060 or 53 or 10061)
            {
                if (!reported)
                    s_waitingForDatabase(_logger, exception.Number, null);
            }
            if (!reported)
            {
                s_waitingForSchema(_logger, null);
                reported = true;
            }
            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining < s_retryInterval ? remaining : s_retryInterval, _time, cancellationToken)
                .ConfigureAwait(false);
        }
        while (true);

        throw new InvalidOperationException(
            "Paper host cannot start: the configured SQL database is unavailable or its schema is incomplete. " +
            "Start the development web bootstrap or apply reviewed deployment migrations.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<bool> HasSchemaAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
                continue;
            var schema = entity.GetSchema() ?? "dbo";
            var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (column is not null)
                    expected.Add($"{schema}.{table}.{column}");
            }
        }
        DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = connection.CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandText = """
                SELECT [s].[name], [t].[name], [c].[name]
                FROM [sys].[tables] AS [t]
                JOIN [sys].[schemas] AS [s] ON [t].[schema_id] = [s].[schema_id]
                JOIN [sys].[columns] AS [c] ON [t].[object_id] = [c].[object_id]
                """;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                expected.Remove($"{reader.GetString(0)}.{reader.GetString(1)}.{reader.GetString(2)}");
            return expected.Count == 0;
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }
}
