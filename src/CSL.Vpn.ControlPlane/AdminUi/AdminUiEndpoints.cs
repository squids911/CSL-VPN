using System.Net;
using System.Text;
using CSL.Vpn.ControlPlane.Security;
using Npgsql;

namespace CSL.Vpn.ControlPlane.AdminUi;

/// <summary>
/// Minimal server-rendered operator UI at /admin/ui. Cookie sessions
/// (HMAC-stamped, see AdminUiAuth), CSRF tokens on every form, strict
/// security headers, and rate-limited login. No JavaScript.
/// </summary>
public static class AdminUiEndpoints
{
    public static void MapAdminUiEndpoints(this WebApplication app)
    {
        app.UseWhen(
            context => context.Request.Path.StartsWithSegments("/admin/ui"),
            branch => branch.Use(SecurityHeadersAsync));

        var ui = app.MapGroup("/admin/ui");

        ui.MapGet("/login", () => Page("CSL VPN — sign in", LoginForm(null)));
        ui.MapPost("/login", LoginAsync).RequireRateLimiting("adminui");
        ui.MapPost("/logout", Logout);

        ui.MapGet("/", DashboardAsync).AddEndpointFilter(AuthFilter);
        ui.MapPost("/codes", CreateCodeAsync)
            .AddEndpointFilter(AuthFilter).AddEndpointFilter(CsrfFilter);
        ui.MapPost("/devices/{deviceId:guid}/revoke", RevokeAsync)
            .AddEndpointFilter(AuthFilter).AddEndpointFilter(CsrfFilter);
    }

    // ---- handlers ----

    private static async Task<IResult> LoginAsync(HttpContext http, IConfiguration configuration)
    {
        var adminKey = configuration["AdminApiKey"];
        if (string.IsNullOrEmpty(adminKey))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var form = await http.Request.ReadFormAsync();
        var key = form["key"].ToString();

        var match = key.Length > 0 && KeysMatch(key, adminKey);
        if (!match)
        {
            return Page("CSL VPN — sign in", LoginForm("Invalid key."), StatusCodes.Status401Unauthorized);
        }

        var cookieValue = AdminUiAuth.IssueCookieValue(adminKey, DateTimeOffset.UtcNow);
        http.Response.Cookies.Append(AdminUiAuth.CookieName, cookieValue, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.Add(AdminUiAuth.SessionLifetime),
            Path = "/admin/ui",
        });
        return Results.Redirect("/admin/ui/");
    }

    private static IResult Logout(HttpContext http)
    {
        http.Response.Cookies.Delete(AdminUiAuth.CookieName, new CookieOptions { Path = "/admin/ui" });
        return Results.Redirect("/admin/ui/login");
    }

    private static async Task<IResult> DashboardAsync(
        HttpContext http, IConfiguration configuration, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        return await RenderDashboardAsync(http, configuration, dataSource, null, cancellationToken);
    }

    private static async Task<IResult> CreateCodeAsync(
        HttpContext http, IConfiguration configuration, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var form = await http.Request.ReadFormAsync(cancellationToken);
        if (!int.TryParse(form["expiresInMinutes"], out var minutes) || minutes is < 1 or > 10080)
        {
            minutes = 60;
        }

        var code = TokenService.NewToken(16);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes);

        await using var command = dataSource.CreateCommand(
            "INSERT INTO enrollment_codes (code_hash, expires_at) VALUES (@hash, @expires)");
        command.Parameters.AddWithValue("hash", TokenService.Hash(code));
        command.Parameters.AddWithValue("expires", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);

        var banner = $"""
            <div class="banner">
              <b>Enrollment code (shown once):</b>
              <code class="code">{E(code)}</code>
              <div class="muted">Expires {Fmt(expiresAt)} UTC. Store only on the target device; the server keeps just the hash.</div>
            </div>
            """;
        return await RenderDashboardAsync(http, configuration, dataSource, banner, cancellationToken);
    }

    private static async Task<IResult> RevokeAsync(
        Guid deviceId, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await DeviceMaintenance.RevokeDeviceAsync(dataSource, deviceId, cancellationToken);
        return Results.Redirect("/admin/ui/");
    }

    // ---- filters ----

    private static ValueTask<object?> AuthFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var adminKey = http.RequestServices.GetRequiredService<IConfiguration>()["AdminApiKey"];
        if (string.IsNullOrEmpty(adminKey)
            || !AdminUiAuth.IsValidCookie(http.Request.Cookies[AdminUiAuth.CookieName], adminKey, DateTimeOffset.UtcNow))
        {
            return ValueTask.FromResult<object?>(Results.Redirect("/admin/ui/login"));
        }

        return next(context);
    }

    private static async ValueTask<object?> CsrfFilter(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var adminKey = http.RequestServices.GetRequiredService<IConfiguration>()["AdminApiKey"]!;
        var cookie = http.Request.Cookies[AdminUiAuth.CookieName] ?? string.Empty;
        var form = await http.Request.ReadFormAsync();

        if (!AdminUiAuth.IsValidCsrfToken(form["csrf"], cookie, adminKey))
        {
            return Results.BadRequest();
        }

        return await next(context);
    }

    // ---- dashboard ----

    private static async Task<IResult> RenderDashboardAsync(
        HttpContext http, IConfiguration configuration, NpgsqlDataSource dataSource,
        string? banner, CancellationToken cancellationToken)
    {
        var adminKey = configuration["AdminApiKey"]!;
        var cookie = http.Request.Cookies[AdminUiAuth.CookieName] ?? string.Empty;
        var csrf = AdminUiAuth.IssueCsrfToken(cookie, adminKey);

        var devices = await LoadDevicesAsync(dataSource, cancellationToken);
        var codes = await LoadCodesAsync(dataSource, cancellationToken);

        var body = new StringBuilder();
        if (banner is not null)
        {
            body.Append(banner);
        }

        body.Append($"""
            <h2>Devices ({devices.Count})</h2>
            <table>
              <tr><th>Name</th><th>Address</th><th>Session</th><th>Last seen (UTC)</th><th>Enrolled (UTC)</th><th>Status</th><th></th></tr>
            """);
        foreach (var device in devices)
        {
            var status = device.RevokedAt is not null ? "<span class=\"bad\">revoked</span>"
                : device.SessionActive ? "<span class=\"ok\">active</span>" : "idle";
            var revoke = device.RevokedAt is null
                ? $"""
                    <form method="post" action="/admin/ui/devices/{device.DeviceId}/revoke" style="display:inline">
                      <input type="hidden" name="csrf" value="{csrf}">
                      <button class="danger" type="submit">Revoke</button>
                    </form>
                    """
                : string.Empty;
            body.Append($"""
                <tr>
                  <td>{E(device.DisplayName)}</td>
                  <td><code>{E(device.Address ?? "—")}</code></td>
                  <td>{(device.SessionActive ? "active" : "—")}</td>
                  <td>{Fmt(device.LastSeenAt)}</td>
                  <td>{Fmt(device.EnrolledAt)}</td>
                  <td>{status}</td>
                  <td>{revoke}</td>
                </tr>
                """);
        }

        body.Append($"""
            </table>

            <h2>Enrollment codes</h2>
            <form method="post" action="/admin/ui/codes" class="inline">
              <input type="hidden" name="csrf" value="{csrf}">
              <label>Expires in <input type="number" name="expiresInMinutes" value="60" min="1" max="10080" size="6"> minutes</label>
              <button type="submit">Create code</button>
            </form>
            <table>
              <tr><th>Created (UTC)</th><th>Expires (UTC)</th><th>State</th></tr>
            """);
        foreach (var code in codes)
        {
            var state = code.Consumed ? "consumed"
                : code.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : "<span class=\"ok\">usable</span>";
            body.Append($"<tr><td>{Fmt(code.CreatedAt)}</td><td>{Fmt(code.ExpiresAt)}</td><td>{state}</td></tr>");
        }

        body.Append("</table>");
        return Page("CSL VPN — admin", body.ToString());
    }

    private static async Task<List<DeviceRow>> LoadDevicesAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var devices = new List<DeviceRow>();
        await using var command = dataSource.CreateCommand("""
            SELECT d.id, d.display_name, d.enrolled_at, d.last_seen_at, d.revoked_at,
                   host(a.address),
                   EXISTS(SELECT 1 FROM sessions s WHERE s.device_id = d.id AND s.ended_at IS NULL)
            FROM devices d
            LEFT JOIN address_pool a ON a.reserved_by = d.id
            ORDER BY d.enrolled_at DESC
            LIMIT 100
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new DeviceRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                await reader.IsDBNullAsync(4, cancellationToken) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                reader.GetBoolean(6)));
        }

        return devices;
    }

    private static async Task<List<CodeRow>> LoadCodesAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var codes = new List<CodeRow>();
        await using var command = dataSource.CreateCommand("""
            SELECT created_at, expires_at, consumed_at IS NOT NULL
            FROM enrollment_codes
            ORDER BY created_at DESC
            LIMIT 50
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            codes.Add(new CodeRow(
                reader.GetFieldValue<DateTimeOffset>(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                reader.GetBoolean(2)));
        }

        return codes;
    }

    // ---- rendering helpers ----

    private sealed record DeviceRow(
        Guid DeviceId, string DisplayName, DateTimeOffset EnrolledAt,
        DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt, string? Address, bool SessionActive);

    private sealed record CodeRow(DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, bool Consumed);

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string Fmt(DateTimeOffset? value) =>
        value is null ? "—" : value.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm");

    private static bool KeysMatch(string provided, string expected) =>
        provided.Length == expected.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));

    private static IResult Page(string title, string body, int statusCode = StatusCodes.Status200OK) =>
        Results.Text($$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{E(title)}}</title>
              <style>
                body { font-family: system-ui, sans-serif; margin: 2rem auto; max-width: 960px; padding: 0 1rem; color: #1a1a1a; }
                table { border-collapse: collapse; width: 100%; margin: 1rem 0 2rem; }
                th, td { border: 1px solid #ccc; padding: .4rem .6rem; text-align: left; font-size: .9rem; }
                th { background: #f2f2f2; }
                .banner { background: #fff8dc; border: 1px solid #d4c76a; padding: 1rem; margin: 1rem 0; }
                .code { font-size: 1.2rem; letter-spacing: .05em; }
                .ok { color: #187a18; font-weight: 600; }
                .bad { color: #a01818; }
                .muted { color: #666; font-size: .85rem; }
                button { padding: .35rem .8rem; }
                .danger { background: #a01818; color: #fff; border: 0; cursor: pointer; }
                form.inline { margin: .5rem 0 1rem; }
                input[type=password], input[type=number] { padding: .35rem; }
              </style>
            </head>
            <body>
              <h1>CSL VPN</h1>
              {{body}}
              <hr>
              <form method="post" action="/admin/ui/logout" class="inline"><button type="submit">Sign out</button></form>
            </body>
            </html>
            """, "text/html; charset=utf-8", statusCode: statusCode);

    private static string LoginForm(string? error) => $"""
        {(error is null ? string.Empty : $"<p class=\"bad\">{E(error)}</p>")}
        <form method="post" action="/admin/ui/login">
          <label>Admin key <input type="password" name="key" autofocus autocomplete="off"></label>
          <button type="submit">Sign in</button>
        </form>
        <p class="muted">Access is logged and rate-limited.</p>
        """;

    private static async Task SecurityHeadersAsync(HttpContext context, Func<Task> next)
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers.CacheControl = "no-store";
        await next();
    }
}
