using CSL.Vpn.ControlPlane.Security;
using CslVpn.Shared.Contracts;
using Npgsql;

namespace CSL.Vpn.ControlPlane;

/// <summary>
/// Device-facing session endpoints. A device holds at most one active
/// session: the partial unique index in 0001_initial.sql is the backstop,
/// and the ON CONFLICT clause turns a reconnect into a heartbeat refresh.
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this WebApplication app)
    {
        app.MapPost("/session/heartbeat", HeartbeatAsync).RequireRateLimiting("session");
        app.MapPost("/session/disconnect", DisconnectAsync).RequireRateLimiting("session");
        app.MapGet("/device/status", StatusAsync);
    }

    private static async Task<IResult> HeartbeatAsync(
        NpgsqlDataSource dataSource, HttpRequest request, CancellationToken cancellationToken)
    {
        var device = await DeviceAuth.ResolveAsync(dataSource, request, cancellationToken);
        if (device is null)
        {
            return Unauthorized();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var touch = new NpgsqlCommand(
            "UPDATE devices SET last_seen_at = now() WHERE id = @id", connection, transaction))
        {
            touch.Parameters.AddWithValue("id", device.DeviceId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid sessionId;
        DateTimeOffset startedAt;
        DateTimeOffset lastHeartbeat;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO sessions (device_id) VALUES (@id)
            ON CONFLICT (device_id) WHERE ended_at IS NULL
            DO UPDATE SET last_heartbeat = now()
            RETURNING id, started_at, last_heartbeat
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", device.DeviceId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            sessionId = reader.GetGuid(0);
            startedAt = reader.GetFieldValue<DateTimeOffset>(1);
            lastHeartbeat = reader.GetFieldValue<DateTimeOffset>(2);
        }

        string? address;
        await using (var command = new NpgsqlCommand(
            "SELECT host(address) FROM address_pool WHERE reserved_by = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("id", device.DeviceId);
            address = (string?)await command.ExecuteScalarAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var status = address is null ? "no_address" : "active";
        return Results.Ok(new HeartbeatResponse(sessionId, startedAt, lastHeartbeat, address, status));
    }

    private static async Task<IResult> DisconnectAsync(
        NpgsqlDataSource dataSource, HttpRequest request, CancellationToken cancellationToken)
    {
        var device = await DeviceAuth.ResolveAsync(dataSource, request, cancellationToken);
        if (device is null)
        {
            return Unauthorized();
        }

        await using var command = dataSource.CreateCommand(
            "UPDATE sessions SET ended_at = now() WHERE device_id = @id AND ended_at IS NULL");
        command.Parameters.AddWithValue("id", device.DeviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return Results.Ok(new { status = "disconnected" });
    }

    private static async Task<IResult> StatusAsync(
        NpgsqlDataSource dataSource, HttpRequest request, CancellationToken cancellationToken)
    {
        var device = await DeviceAuth.ResolveAsync(dataSource, request, cancellationToken);
        if (device is null)
        {
            return Unauthorized();
        }

        await using var command = dataSource.CreateCommand("""
            SELECT d.enrolled_at, d.last_seen_at, host(a.address),
                   EXISTS(SELECT 1 FROM sessions s WHERE s.device_id = d.id AND s.ended_at IS NULL)
            FROM devices d
            LEFT JOIN address_pool a ON a.reserved_by = d.id
            WHERE d.id = @id
            """);
        command.Parameters.AddWithValue("id", device.DeviceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Unauthorized();
        }

        var enrolledAt = reader.GetFieldValue<DateTimeOffset>(0);
        var lastSeenAt = await reader.IsDBNullAsync(1, cancellationToken)
            ? (DateTimeOffset?)null
            : reader.GetFieldValue<DateTimeOffset>(1);
        var address = await reader.IsDBNullAsync(2, cancellationToken) ? null : reader.GetString(2);
        var sessionActive = reader.GetBoolean(3);

        return Results.Ok(new DeviceStatusResponse(
            device.DeviceId,
            device.DisplayName,
            enrolledAt,
            lastSeenAt,
            address,
            sessionActive));
    }

    private static IResult Unauthorized() =>
        Results.Json(new ErrorResponse("unauthorized"), statusCode: StatusCodes.Status401Unauthorized);
}
