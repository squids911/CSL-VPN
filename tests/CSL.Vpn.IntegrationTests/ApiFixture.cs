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

    public const string RadiusSecret = "integration-test-radius-secret";

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
                ["Radius:SharedSecret"] = RadiusSecret,
                ["Radius:Port"] = "0",
            });
        });
    }

    /// <summary>
    /// Resets the schema. Migrations themselves are applied by the API's
    /// MigrationRunner when the host starts (first CreateClient), which is
    /// exactly the path under test.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(ConnectionString);
        await using var reset = dataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        await reset.ExecuteNonQueryAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
    }
}
