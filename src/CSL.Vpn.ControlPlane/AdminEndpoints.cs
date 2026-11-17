using CSL.Vpn.ControlPlane.Security;
using Npgsql;

namespace CSL.Vpn.ControlPlane;

/// <summary>
/// Operator endpoints under /admin, guarded by the X-Admin-Key header.
/// Enrollment codes are returned in the clear exactly once; only their
/// hashes are stored.
/// </summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/admin").AddEndpointFilter<AdminKeyFilter>();

        admin.MapPost("/enrollment-codes", CreateEnrollmentCodeAsync);
        admin.MapGet("/devices", ListDevicesAsync);
        admin.MapPost("/devices/{deviceId:guid}/revoke", RevokeDeviceAsync);
    }

    private static async Task<IResult> CreateEnrollmentCodeAsync(
        int? expiresInMinutes,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var minutes = expiresInMinutes ?? 60;
        if (minutes is < 1 or > 10080)
        {
            return Results.Json(
                new { error = "invalid_expiry" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var code = TokenService.NewToken(16);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(minutes);

        await using var command = dataSource.CreateCommand(
            "INSERT INTO enrollment_codes (code_hash, expires_at) VALUES (@hash, @expires)");
        command.Parameters.AddWithValue("hash", TokenService.Hash(code));
        command.Parameters.AddWithValue("expires", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return Results.Ok(new { code, expiresAt });
    }

    private static async Task<IResult> ListDevicesAsync(
        NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var devices = new List<object>();

        await using var command = dataSource.CreateCommand("""
            SELECT d.id, d.display_name, d.enrolled_at, d.last_seen_at, d.revoked_at,
                   host(a.address),
                   EXISTS(SELECT 1 FROM sessions s WHERE s.device_id = d.id AND s.ended_at IS NULL)
            FROM devices d
            LEFT JOIN address_pool a ON a.reserved_by = d.id
            ORDER BY d.enrolled_at DESC
            LIMIT 500
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new
            {
                deviceId = reader.GetGuid(0),
                displayName = reader.GetString(1),
                enrolledAt = reader.GetFieldValue<DateTimeOffset>(2),
                lastSeenAt = await reader.IsDBNullAsync(3, cancellationToken)
                    ? (DateTimeOffset?)null
                    : reader.GetFieldValue<DateTimeOffset>(3),
                revokedAt = await reader.IsDBNullAsync(4, cancellationToken)
                    ? (DateTimeOffset?)null
                    : reader.GetFieldValue<DateTimeOffset>(4),
                address = await reader.IsDBNullAsync(5, cancellationToken) ? null : reader.GetString(5),
                sessionActive = reader.GetBoolean(6),
            });
        }

        return Results.Ok(new { devices });
    }

    private static async Task<IResult> RevokeDeviceAsync(
        Guid deviceId, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var result = await DeviceMaintenance.RevokeDeviceAsync(dataSource, deviceId, cancellationToken);
        return result switch
        {
            DeviceMaintenance.RevokeResult.NotFound => Results.Json(
                new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound),
            DeviceMaintenance.RevokeResult.AlreadyRevoked => Results.Ok(
                new { deviceId, status = "already_revoked" }),
            _ => Results.Ok(new { deviceId, status = "revoked" }),
        };
    }
}
