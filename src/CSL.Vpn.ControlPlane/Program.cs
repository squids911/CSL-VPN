using CSL.Vpn.ControlPlane;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(serviceProvider =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "Connection string 'Postgres' is not configured. Set ConnectionStrings__Postgres.");

    return NpgsqlDataSource.Create(connectionString);
});

builder.Services.AddHostedService<CleanupService>();

var app = builder.Build();

// Liveness: the API process is running. No dependencies are touched.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

// Readiness: also verifies that PostgreSQL accepts a trivial query.
app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    IResult result;
    try
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(cancellationToken);
        result = Results.Ok(new { status = "ready" });
    }
    catch (NpgsqlException)
    {
        result = Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    return result;
});

app.MapEnrollmentEndpoints();
app.MapSessionEndpoints();
app.MapAdminEndpoints();

await app.RunAsync();

// Exposes the implicit Program class so WebApplicationFactory<Program> can host
// the API in the integration-test project.
public partial class Program
{
}
