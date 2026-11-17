using Npgsql;

namespace CSL.Vpn.ControlPlane;

/// <summary>Shared device lifecycle operations used by the admin API and the cleanup sweeper.</summary>
public static class DeviceMaintenance
{
    public enum RevokeResult
    {
        Revoked,
        AlreadyRevoked,
        NotFound,
    }

    /// <summary>
    /// Revokes a device: invalidates its credentials, ends its active session,
    /// and returns its address to the pool. Idempotent.
    /// </summary>
    public static async Task<RevokeResult> RevokeDeviceAsync(
        NpgsqlDataSource dataSource, Guid deviceId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lookup = new NpgsqlCommand(
            "SELECT revoked_at FROM devices WHERE id = @id FOR UPDATE", connection, transaction))
        {
            lookup.Parameters.AddWithValue("id", deviceId);
            var revokedAt = await lookup.ExecuteScalarAsync(cancellationToken);
            switch (revokedAt)
            {
                case null:
                    return RevokeResult.NotFound;
                case not DBNull:
                    return RevokeResult.AlreadyRevoked;
            }
        }

        await using (var command = new NpgsqlCommand("""
            UPDATE devices SET revoked_at = now() WHERE id = @id;
            UPDATE device_credentials SET revoked_at = now() WHERE device_id = @id AND revoked_at IS NULL;
            UPDATE sessions SET ended_at = now() WHERE device_id = @id AND ended_at IS NULL;
            UPDATE address_pool SET reserved_by = NULL, reserved_at = NULL WHERE reserved_by = @id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("id", deviceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return RevokeResult.Revoked;
    }

    /// <summary>
    /// Revokes every device whose last activity (or enrollment, if never seen)
    /// is older than <paramref name="maxOffline"/>. Returns the number revoked.
    /// </summary>
    public static async Task<int> RevokeExpiredDevicesAsync(
        NpgsqlDataSource dataSource, TimeSpan maxOffline, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            WITH expired AS (
                UPDATE devices
                SET revoked_at = now()
                WHERE revoked_at IS NULL
                  AND COALESCE(last_seen_at, enrolled_at) < now() - @maxOffline::interval
                RETURNING id
            ),
            creds AS (
                UPDATE device_credentials SET revoked_at = now()
                WHERE revoked_at IS NULL AND device_id IN (SELECT id FROM expired)
            ),
            sess AS (
                UPDATE sessions SET ended_at = now()
                WHERE ended_at IS NULL AND device_id IN (SELECT id FROM expired)
            ),
            pool AS (
                UPDATE address_pool SET reserved_by = NULL, reserved_at = NULL
                WHERE reserved_by IN (SELECT id FROM expired)
            )
            SELECT count(*) FROM expired
            """);
        command.Parameters.AddWithValue("maxOffline", maxOffline);

        var count = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(count);
    }
}
