using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// Hosts the real control-plane API against a real PostgreSQL (the CI service
/// container). Applies the production bootstrap migration to a fresh schema.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AdminKey = "integration-test-admin-key";

    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=cslvpn_test;Username=postgres;Password=postgres;Timeout=5;Command Timeout=15";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("CSLVPN_TEST_DB") ?? DefaultConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = ConnectionString,
                ["AdminApiKey"] = AdminKey,
            });
        });
    }

    public async Task InitializeAsync()
    {
        var migrations = Directory.GetFiles(AppContext.BaseDirectory, "*.sql")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        if (migrations.Length == 0)
        {
            throw new InvalidOperationException("No database migrations found in the test output directory.");
        }

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

        await using (var reset = dataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;"))
        {
            await reset.ExecuteNonQueryAsync();
        }

        foreach (var path in migrations)
        {
            await using var migration = dataSource.CreateCommand(await File.ReadAllTextAsync(path));
            await migration.ExecuteNonQueryAsync();
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
