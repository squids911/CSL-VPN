using System.Reflection;
using Npgsql;

namespace CSL.Vpn.ControlPlane;

/// <summary>
/// Applies db/migrations embedded in the assembly, in file-name order, and
/// records them in schema_migrations. Migrations carry their own
/// transactions (BEGIN/COMMIT in the .sql files), so each file is all or
/// nothing; the marker row is written right after.
///
/// Baseline rule for databases that predate the migrator: if the devices
/// table already exists, 0001 is recorded as applied without re-running, and
/// 0002 is recorded too when the gateway address is already gone. Databases
/// created after 0002 (0002 is idempotent) converge through the normal path.
/// </summary>
public static class MigrationRunner
{
    private const string ResourcePrefix = "CSL.Vpn.db.migrations.";

    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource, ILogger logger, CancellationToken cancellationToken = default)
    {
        var migrations = LoadEmbedded();
        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("No embedded database migrations found.");
        }

        await using var connection = await OpenWithRetryAsync(dataSource, logger, cancellationToken);

        await using (var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                name        TEXT PRIMARY KEY,
                applied_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = await LoadAppliedAsync(connection, cancellationToken);
        if (applied.Count == 0)
        {
            await ApplyBaselineIfLegacyAsync(connection, applied, logger, cancellationToken);
        }

        foreach (var (name, sql) in migrations)
        {
            if (applied.Contains(name))
            {
                continue;
            }

            logger.LogInformation("Applying database migration {Migration}.", name);
            await using (var command = new NpgsqlCommand(sql, connection))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var marker = new NpgsqlCommand(
                "INSERT INTO schema_migrations (name) VALUES (@name)", connection))
            {
                marker.Parameters.AddWithValue("name", name);
                await marker.ExecuteNonQueryAsync(cancellationToken);
            }

            logger.LogInformation("Migration {Migration} applied.", name);
        }
    }

    private static async Task<NpgsqlConnection> OpenWithRetryAsync(
        NpgsqlDataSource dataSource, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dataSource.OpenConnectionAsync(cancellationToken);
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}); retrying.", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private static async Task<HashSet<string>> LoadAppliedAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand("SELECT name FROM schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static async Task ApplyBaselineIfLegacyAsync(
        NpgsqlConnection connection, HashSet<string> applied, ILogger logger, CancellationToken cancellationToken)
    {
        var devicesExists = await ScalarAsync<bool>(
            connection, "SELECT to_regclass('public.devices') IS NOT NULL", cancellationToken);
        if (!devicesExists)
        {
            return; // Fresh database: every migration runs normally.
        }

        await MarkAppliedAsync(connection, applied, "0001_initial.sql", cancellationToken);
        logger.LogInformation("Baseline: existing database detected, recorded 0001_initial.sql as applied.");

        var gatewayGone = await ScalarAsync<bool>(
            connection,
            "SELECT NOT EXISTS (SELECT 1 FROM address_pool WHERE address = '10.255.0.1')",
            cancellationToken);
        if (gatewayGone)
        {
            await MarkAppliedAsync(connection, applied, "0002_exclude_gateway.sql", cancellationToken);
            logger.LogInformation("Baseline: gateway address already excluded, recorded 0002_exclude_gateway.sql as applied.");
        }
    }

    private static async Task MarkAppliedAsync(
        NpgsqlConnection connection, HashSet<string> applied, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO schema_migrations (name) VALUES (@name) ON CONFLICT DO NOTHING", connection);
        command.Parameters.AddWithValue("name", name);
        await command.ExecuteNonQueryAsync(cancellationToken);
        applied.Add(name);
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static List<(string Name, string Sql)> LoadEmbedded()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var migrations = new List<(string, string)>();

        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded migration {resource} is unreadable.");
            using var reader = new StreamReader(stream);
            migrations.Add((resource[ResourcePrefix.Length..], reader.ReadToEnd()));
        }

        return migrations;
    }
}
