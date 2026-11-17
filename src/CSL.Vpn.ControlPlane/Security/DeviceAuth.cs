using Npgsql;

namespace CSL.Vpn.ControlPlane.Security;

/// <summary>Identity of an enrolled, non-revoked device resolved from a bearer credential.</summary>
public sealed record DeviceIdentity(Guid DeviceId, string DisplayName);

public static class DeviceAuth
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Resolves the device behind an Authorization: Bearer credential.
    /// Returns null when the credential is missing, unknown, revoked, or its
    /// device is revoked. Only credential hashes are consulted.
    /// </summary>
    public static async Task<DeviceIdentity?> ResolveAsync(
        NpgsqlDataSource dataSource,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var token = header[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return null;
        }

        var hash = TokenService.Hash(token);

        await using var command = dataSource.CreateCommand("""
            SELECT d.id, d.display_name
            FROM device_credentials c
            JOIN devices d ON d.id = c.device_id
            WHERE c.secret_hash = @hash
              AND c.revoked_at IS NULL
              AND d.revoked_at IS NULL
            """);
        command.Parameters.AddWithValue("hash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceIdentity(reader.GetGuid(0), reader.GetString(1));
    }
}
