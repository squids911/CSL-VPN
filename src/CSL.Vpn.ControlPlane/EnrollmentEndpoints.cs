using CSL.Vpn.ControlPlane.Security;
using Npgsql;

namespace CSL.Vpn.ControlPlane;

/// <summary>
/// POST /enroll — one-time enrollment code in, device credential and VPN
/// address out. Everything happens in one database transaction: the code is
/// locked, validated and consumed; the address allocation uses
/// FOR UPDATE SKIP LOCKED so concurrent enrollments never share a lease.
/// </summary>
public static class EnrollmentEndpoints
{
    public static void MapEnrollmentEndpoints(this WebApplication app)
    {
        app.MapPost("/enroll", EnrollAsync);
    }

    private static async Task<IResult> EnrollAsync(
        EnrollRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 128)
        {
            return Error("invalid_code");
        }

        var displayName = (request.DisplayName ?? string.Empty).Trim();
        if (displayName.Length is 0 or > 100)
        {
            return Error("invalid_display_name");
        }

        if (request.DeviceId == Guid.Empty)
        {
            return Error("invalid_device_id");
        }

        var codeHash = TokenService.Hash(request.Code);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1. Lock and validate the enrollment code.
        Guid codeId;
        DateTimeOffset codeExpiresAt;
        bool codeConsumed;
        await using (var command = new NpgsqlCommand("""
            SELECT id, expires_at, consumed_by
            FROM enrollment_codes
            WHERE code_hash = @hash
            FOR UPDATE
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("hash", codeHash);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Error("invalid_code");
            }

            codeId = reader.GetGuid(0);
            codeExpiresAt = reader.GetFieldValue<DateTimeOffset>(1);
            codeConsumed = !await reader.IsDBNullAsync(2, cancellationToken);
        }

        if (codeConsumed)
        {
            return Error("code_consumed");
        }

        if (codeExpiresAt <= DateTimeOffset.UtcNow)
        {
            return Error("code_expired");
        }

        // 2. Create the device, or re-validate a preserved identity (reinstall).
        Guid deviceId;
        if (request.DeviceId is Guid requestedId)
        {
            await using var lookup = new NpgsqlCommand(
                "SELECT revoked_at FROM devices WHERE id = @id FOR UPDATE", connection, transaction);
            lookup.Parameters.AddWithValue("id", requestedId);
            var revokedAt = await lookup.ExecuteScalarAsync(cancellationToken);

            switch (revokedAt)
            {
                case null:
                    // First enrollment with a client-preserved identity.
                    await InsertDeviceAsync(connection, transaction, requestedId, displayName, cancellationToken);
                    break;
                case DBNull:
                    // Known active device re-enrolling with its preserved identity.
                    break;
                default:
                    return Error("device_revoked", StatusCodes.Status403Forbidden);
            }

            deviceId = requestedId;
        }
        else
        {
            deviceId = Guid.NewGuid();
            await InsertDeviceAsync(connection, transaction, deviceId, displayName, cancellationToken);
        }

        // 3. Keep the existing reservation or allocate an address atomically.
        var address = await GetReservedAddressAsync(connection, transaction, deviceId, cancellationToken)
            ?? await AllocateAddressAsync(connection, transaction, deviceId, cancellationToken);

        if (address is null)
        {
            return Error("pool_exhausted", StatusCodes.Status503ServiceUnavailable);
        }

        // 4. Re-enrollment invalidates previous credentials and sessions.
        await ExecuteForDeviceAsync(connection, transaction,
            "UPDATE device_credentials SET revoked_at = now() WHERE device_id = @id AND revoked_at IS NULL",
            deviceId, cancellationToken);
        await ExecuteForDeviceAsync(connection, transaction,
            "UPDATE sessions SET ended_at = now() WHERE device_id = @id AND ended_at IS NULL",
            deviceId, cancellationToken);

        // 5. Issue a fresh credential: hash at rest, raw value returned once.
        var credential = TokenService.NewToken();
        await using (var command = new NpgsqlCommand(
            "INSERT INTO device_credentials (device_id, secret_hash) VALUES (@id, @hash)", connection, transaction))
        {
            command.Parameters.AddWithValue("id", deviceId);
            command.Parameters.AddWithValue("hash", TokenService.Hash(credential));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // 6. Consume the code so it can never be reused.
        await using (var command = new NpgsqlCommand(
            "UPDATE enrollment_codes SET consumed_by = @device, consumed_at = now() WHERE id = @code",
            connection, transaction))
        {
            command.Parameters.AddWithValue("device", deviceId);
            command.Parameters.AddWithValue("code", codeId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(new
        {
            deviceId,
            displayName,
            address,
            credential,
        });
    }

    private static async Task InsertDeviceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid deviceId, string displayName, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO devices (id, display_name) VALUES (@id, @name)", connection, transaction);
        command.Parameters.AddWithValue("id", deviceId);
        command.Parameters.AddWithValue("name", displayName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> GetReservedAddressAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT host(address) FROM address_pool WHERE reserved_by = @id", connection, transaction);
        command.Parameters.AddWithValue("id", deviceId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<string?> AllocateAddressAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE address_pool
            SET reserved_by = @id, reserved_at = now()
            WHERE address = (
                SELECT address FROM address_pool
                WHERE reserved_by IS NULL
                ORDER BY address
                LIMIT 1
                FOR UPDATE SKIP LOCKED)
            RETURNING host(address)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", deviceId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteForDeviceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IResult Error(string code, int statusCode = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = code }, statusCode: statusCode);

    public sealed record EnrollRequest(string? Code, string? DisplayName, Guid? DeviceId);
}
