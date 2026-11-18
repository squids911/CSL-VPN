using Npgsql;
using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// The API applies embedded migrations at startup. Against the reset schema
/// the fixture provides, the host must have run the full chain and recorded
/// every migration exactly once.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MigrationRunnerTests(ApiFixture fixture)
{
    [Fact]
    public async Task All_embedded_migrations_are_applied_and_recorded()
    {
        // Touch the factory so the host (and its MigrationRunner) has started.
        _ = fixture.Services;

        await using var dataSource = NpgsqlDataSource.Create(ApiFixture.ConnectionString);
        await using var command = dataSource.CreateCommand("SELECT name FROM schema_migrations ORDER BY name");

        var applied = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            applied.Add(reader.GetString(0));
        }

        Assert.Equal(new[] { "0001_initial.sql", "0002_exclude_gateway.sql" }, applied);
    }
}
