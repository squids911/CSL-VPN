using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

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
        var schema = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "0001_initial.sql"));

        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);

        await using (var reset = dataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;"))
        {
            await reset.ExecuteNonQueryAsync();
        }

        await using (var migration = dataSource.CreateCommand(schema))
        {
            await migration.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
