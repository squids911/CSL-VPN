using System.Threading.RateLimiting;
using CSL.Vpn.ControlPlane;
using CslVpn.Shared.Contracts;
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

// RADIUS facade for the VPN data plane: SoftEther delegates authentication
// to this service (wildcard "*" user). Enabled only when Radius:SharedSecret
// is configured; binds UDP inside the container and is published on loopback.
builder.Services.AddSingleton<CSL.Vpn.ControlPlane.Security.RadiusServer>();
builder.Services.AddHostedService(serviceProvider =>
    serviceProvider.GetRequiredService<CSL.Vpn.ControlPlane.Security.RadiusServer>());

// Brute-force protection. The API sits behind Nginx Proxy Manager, so the
// client partition key is the first X-Forwarded-For hop, falling back to the
// direct peer address. Limits are deliberately generous for legitimate use:
// a device enrolls once and heartbeats every 60 seconds.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("rate_limited"), cancellationToken));
    };
    options.AddPolicy("enrollment", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    options.AddPolicy("session", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartitionKey(httpContext),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

// Apply pending database migrations before serving traffic. Fresh databases
// get the full chain; pre-migrator databases are baselined (see runner).
await MigrationRunner.ApplyAsync(
    app.Services.GetRequiredService<NpgsqlDataSource>(), app.Logger);

app.UseRateLimiter();

static string ClientPartitionKey(HttpContext context)
{
    var forwardedFor = context.Request.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrWhiteSpace(forwardedFor))
    {
        var firstHop = forwardedFor.Split(',', StringSplitOptions.TrimEntries)[0];
        if (firstHop.Length > 0)
        {
            return firstHop;
        }
    }

    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

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
