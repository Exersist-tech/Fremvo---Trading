using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Trading.Infrastructure.Data;

public sealed record TradingSqlSchemaComparison(
    IReadOnlyList<string> MissingFromTarget,
    IReadOnlyList<string> UnexpectedInTarget)
{
    public bool Matches => MissingFromTarget.Count == 0 && UnexpectedInTarget.Count == 0;
}

/// <summary>
/// Read-only preflight for an operator reviewing migration adoption on an EnsureCreated database.
/// A matching catalog is necessary, but never sufficient, to authorize changing migration history.
/// </summary>
public static class TradingSqlSchemaComparer
{
    private static readonly string[] s_catalogQueries =
    [
        """
        SELECT 'database', CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')),
            compatibility_level, is_read_committed_snapshot_on, snapshot_isolation_state
        FROM sys.databases WHERE name = DB_NAME()
        """,
        """
        SELECT 'table', s.name, t.name, t.temporal_type_desc, t.is_memory_optimized
        FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
        """,
        """
        SELECT 'column', s.name, t.name, c.name, ty.name, c.max_length,
            c.precision, c.scale, c.is_nullable, c.collation_name, c.is_identity,
            c.is_computed, c.is_rowguidcol, c.generated_always_type_desc,
            d.definition, computed.definition, identity_column.seed_value, identity_column.increment_value
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints d ON d.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns computed ON computed.object_id = c.object_id AND computed.column_id = c.column_id
        LEFT JOIN sys.identity_columns identity_column ON identity_column.object_id = c.object_id AND identity_column.column_id = c.column_id
        WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
        """,
        """
        SELECT 'index', s.name, t.name, i.name, i.type_desc, i.is_unique,
            i.is_primary_key, i.is_unique_constraint, i.has_filter, i.filter_definition,
            i.is_disabled, ic.key_ordinal, ic.is_included_column, ic.is_descending_key,
            ic.index_column_id, c.name
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        LEFT JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory' AND i.index_id > 0
        """,
        """
        SELECT 'foreign-key', s.name, t.name, fk.name, target_schema.name, target_table.name,
            fk.delete_referential_action_desc, fk.update_referential_action_desc,
            fk.is_disabled, fk.is_not_trusted, fkc.constraint_column_id,
            source_column.name, target_column.name
        FROM sys.foreign_keys fk
        JOIN sys.tables t ON t.object_id = fk.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.tables target_table ON target_table.object_id = fk.referenced_object_id
        JOIN sys.schemas target_schema ON target_schema.schema_id = target_table.schema_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns source_column ON source_column.object_id = t.object_id AND source_column.column_id = fkc.parent_column_id
        JOIN sys.columns target_column ON target_column.object_id = target_table.object_id AND target_column.column_id = fkc.referenced_column_id
        WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
        """,
        """
        SELECT 'check', s.name, t.name, ch.name, ch.definition, ch.is_disabled, ch.is_not_trusted
        FROM sys.check_constraints ch
        JOIN sys.tables t ON t.object_id = ch.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
        """,
        """
        SELECT 'trigger', s.name, t.name, tr.name, tr.is_disabled, OBJECT_DEFINITION(tr.object_id)
        FROM sys.triggers tr
        JOIN sys.tables t ON t.object_id = tr.parent_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_ms_shipped = 0 AND t.name <> '__EFMigrationsHistory'
        """
    ];

    public static async Task<TradingSqlSchemaComparison> CompareAsync(
        TradingDbContext target, TradingDbContext migratedReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(migratedReference);
        if (!target.Database.IsSqlServer() || !migratedReference.Database.IsSqlServer())
            throw new ArgumentException("Both schema connections must use SQL Server.");
        if (string.Equals(target.Database.GetDbConnection().ConnectionString,
                migratedReference.Database.GetDbConnection().ConnectionString, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Target and reference must use different database connections.");
        var expected = migratedReference.Database.GetMigrations().ToArray();
        var applied = (await migratedReference.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToArray();
        if (expected.Length == 0 || !expected.SequenceEqual(applied))
            throw new InvalidOperationException("The reference database must have every checked-in migration applied.");
        if ((await target.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
            throw new InvalidOperationException("The target already has migration history; use normal migrations instead.");

        await target.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await migratedReference.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await CanViewDatabaseDefinitionAsync(target, cancellationToken).ConfigureAwait(false)
                || !await CanViewDatabaseDefinitionAsync(migratedReference, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Both connections require database VIEW DEFINITION to compare the full schema.");
            if (await HasHistoryTableAsync(target, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The target has a migration history table; review it before adopting migrations.");
            var targetCatalog = await ReadCatalogAsync(target, cancellationToken).ConfigureAwait(false);
            var referenceCatalog = await ReadCatalogAsync(migratedReference, cancellationToken).ConfigureAwait(false);
            if (targetCatalog.Count == 0 || referenceCatalog.Count == 0)
                throw new InvalidOperationException("Both databases must contain a populated trading schema.");
            return new TradingSqlSchemaComparison(
                referenceCatalog.Except(targetCatalog).Order(StringComparer.Ordinal).ToArray(),
                targetCatalog.Except(referenceCatalog).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            await migratedReference.Database.CloseConnectionAsync().ConfigureAwait(false);
            await target.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> CanViewDatabaseDefinitionAsync(TradingDbContext context, CancellationToken token)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText = "SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION')";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<bool> HasHistoryTableAsync(TradingDbContext context, CancellationToken token)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        await using var commandLifetime = command.ConfigureAwait(false);
        command.CommandText = "SELECT COUNT(*) FROM sys.tables WHERE name = '__EFMigrationsHistory'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<HashSet<string>> ReadCatalogAsync(TradingDbContext context, CancellationToken token)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in s_catalogQueries)
        {
            var command = context.Database.GetDbConnection().CreateCommand();
            await using var commandLifetime = command.ConfigureAwait(false);
            command.CommandType = CommandType.Text;
#pragma warning disable CA2100 // Queries are fixed private constants, not operator input.
            command.CommandText = query;
#pragma warning restore CA2100
            var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await using var readerLifetime = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var fields = new string[reader.FieldCount];
                for (var index = 0; index < fields.Length; index++)
                {
                    var value = await reader.IsDBNullAsync(index, token).ConfigureAwait(false) ? null
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
                    fields[index] = value is null ? "-1:" : $"{value.Length}:{value}";
                }
                result.Add(string.Join("|", fields));
            }
        }
        return result;
    }
}
