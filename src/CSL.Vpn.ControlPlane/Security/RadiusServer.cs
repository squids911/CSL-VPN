using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CSL.Vpn.ControlPlane.Security;

/// <summary>
/// Minimal RADIUS PAP server (RFC 2865) that authenticates SoftEther VPN
/// sessions against the control-plane database. SoftEther's hub "CSL" is
/// configured with a wildcard user "*" whose auth type is RADIUS, so every
/// VPN login arrives here as an Access-Request: User-Name is the device GUID,
/// User-Password is the device credential. A credential is accepted only if
/// its SHA-256 hash matches a non-revoked credential of a non-revoked device.
///
/// The listener is intended for loopback or LAN-only binding (SoftEther runs
/// on the same host). PAP passwords are MD5-obfuscated on the wire, which is
/// acceptable only because the path never leaves the host; the credential
/// itself remains a high-entropy token, never a human password.
///
/// Disabled unless Radius:SharedSecret is configured.
/// </summary>
public sealed class RadiusServer(
    IConfiguration configuration,
    NpgsqlDataSource dataSource,
    ILogger<RadiusServer> logger) : BackgroundService
{
    private readonly string? _sharedSecret = configuration["Radius:SharedSecret"];
    private readonly int _port = configuration.GetValue("Radius:Port", 1812);

    /// <summary>The endpoint the UDP socket actually bound (port may be 0/ephemeral in tests).</summary>
    public IPEndPoint? BoundEndpoint { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_sharedSecret))
        {
            logger.LogInformation("RADIUS auth facade disabled (Radius:SharedSecret is not set).");
            return;
        }

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        BoundEndpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        logger.LogInformation("RADIUS auth facade listening on udp/{Port}.", BoundEndpoint.Port);

        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await HandleAsync(udp, received, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Discarding malformed RADIUS datagram from {Remote}.", received.RemoteEndPoint);
            }
        }
    }

    private async Task HandleAsync(UdpClient udp, UdpReceiveResult received, CancellationToken cancellationToken)
    {
        if (!RadiusPacket.TryParseAccessRequest(received.Buffer, out var request))
        {
            return;
        }

        var accepted = false;
        var username = request.GetAttributeString(RadiusPacket.AttrUserName);
        var encryptedPassword = request.GetAttribute(RadiusPacket.AttrUserPassword);

        if (username is not null && encryptedPassword is not null)
        {
            var password = RadiusPacket.DecodeUserPassword(encryptedPassword, request.Authenticator, _sharedSecret!);
            if (Guid.TryParse(username, out var deviceId))
            {
                accepted = await CredentialValidAsync(deviceId, password, cancellationToken);
            }
        }

        logger.LogInformation(
            "RADIUS {Result} for user {Username} from {Remote}.",
            accepted ? "accept" : "reject", username ?? "<none>", received.RemoteEndPoint);

        var response = RadiusPacket.BuildResponse(
            accepted ? RadiusPacket.CodeAccessAccept : RadiusPacket.CodeAccessReject,
            request, _sharedSecret!);
        await udp.SendAsync(response, received.RemoteEndPoint, cancellationToken);
    }

    private async Task<bool> CredentialValidAsync(Guid deviceId, string credential, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM device_credentials dc
            JOIN devices d ON d.id = dc.device_id
            WHERE dc.device_id = @id
              AND dc.secret_hash = @hash
              AND dc.revoked_at IS NULL
              AND d.revoked_at IS NULL
            LIMIT 1
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", deviceId);
        command.Parameters.AddWithValue("hash", TokenService.Hash(credential));
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }
}

/// <summary>Codec for the RADIUS packet subset the facade needs (RFC 2865).</summary>
public static class RadiusPacket
{
    public const byte CodeAccessRequest = 1;
    public const byte CodeAccessAccept = 2;
    public const byte CodeAccessReject = 3;

    public const byte AttrUserName = 1;
    public const byte AttrUserPassword = 2;

    public sealed class AccessRequest
    {
        public required byte Identifier { get; init; }
        public required byte[] Authenticator { get; init; }
        public required IReadOnlyList<(byte Type, byte[] Value)> Attributes { get; init; }

        public byte[]? GetAttribute(byte type) =>
            Attributes.FirstOrDefault(a => a.Type == type).Value;

        public string? GetAttributeString(byte type)
        {
            var value = GetAttribute(type);
            return value is null ? null : Encoding.UTF8.GetString(value);
        }
    }

    public static bool TryParseAccessRequest(byte[] datagram, out AccessRequest request)
    {
        request = null!;
        if (datagram.Length < 20 || datagram[0] != CodeAccessRequest)
        {
            return false;
        }

        var declaredLength = (datagram[2] << 8) | datagram[3];
        if (declaredLength != datagram.Length)
        {
            return false;
        }

        var attributes = new List<(byte, byte[])>();
        var offset = 20;
        while (offset < datagram.Length)
        {
            if (offset + 2 > datagram.Length)
            {
                return false;
            }

            var attributeLength = datagram[offset + 1];
            if (attributeLength < 2 || offset + attributeLength > datagram.Length)
            {
                return false;
            }

            var value = new byte[attributeLength - 2];
            Array.Copy(datagram, offset + 2, value, 0, value.Length);
            attributes.Add((datagram[offset], value));
            offset += attributeLength;
        }

        var authenticator = new byte[16];
        Array.Copy(datagram, 4, authenticator, 0, 16);
        request = new AccessRequest
        {
            Identifier = datagram[1],
            Authenticator = authenticator,
            Attributes = attributes,
        };
        return true;
    }

    /// <summary>Reverses the RFC 2865 §5.2 User-Password obfuscation.</summary>
    public static string DecodeUserPassword(byte[] encrypted, byte[] requestAuthenticator, string sharedSecret)
    {
        if (encrypted.Length is 0 or > 128 || encrypted.Length % 16 != 0)
        {
            throw new FormatException("User-Password must be 16..128 bytes in 16-byte chunks.");
        }

        var secret = Encoding.UTF8.GetBytes(sharedSecret);
        var plain = new byte[encrypted.Length];
        var previous = requestAuthenticator;

        for (var chunk = 0; chunk < encrypted.Length / 16; chunk++)
        {
            var round = MD5.HashData([.. secret, .. previous]);
            for (var i = 0; i < 16; i++)
            {
                plain[chunk * 16 + i] = (byte)(encrypted[chunk * 16 + i] ^ round[i]);
            }

            previous = encrypted.AsSpan(chunk * 16, 16).ToArray();
        }

        var length = Array.IndexOf(plain, (byte)0);
        if (length < 0)
        {
            length = plain.Length;
        }

        return Encoding.UTF8.GetString(plain, 0, length);
    }

    /// <summary>Builds an Access-Request. Used by tests and diagnostics tooling.</summary>
    public static byte[] CreateAccessRequest(
        byte identifier, string username, string password, string sharedSecret, byte[]? requestAuthenticator = null)
    {
        var authenticator = requestAuthenticator ?? RandomNumberGenerator.GetBytes(16);
        var usernameBytes = Encoding.UTF8.GetBytes(username);
        var passwordBytes = EncodeUserPassword(password, authenticator, sharedSecret);

        var length = 20 + 2 + usernameBytes.Length + 2 + passwordBytes.Length;
        using var stream = new MemoryStream(length);
        stream.WriteByte(CodeAccessRequest);
        stream.WriteByte(identifier);
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)(length & 0xFF));
        stream.Write(authenticator);
        WriteAttribute(stream, AttrUserName, usernameBytes);
        WriteAttribute(stream, AttrUserPassword, passwordBytes);
        return stream.ToArray();
    }

    /// <summary>Builds an Access-Accept/Reject with a valid Response Authenticator.</summary>
    public static byte[] BuildResponse(byte code, AccessRequest request, string sharedSecret)
    {
        const int length = 20;
        var header = new byte[length];
        header[0] = code;
        header[1] = request.Identifier;
        header[2] = 0;
        header[3] = length;

        // ResponseAuth = MD5(Code + Identifier + Length + RequestAuth + Attributes + Secret)
        var hash = MD5.HashData([
            header[0], header[1], header[2], header[3],
            .. request.Authenticator,
            .. Encoding.UTF8.GetBytes(sharedSecret),
        ]);
        Array.Copy(hash, 0, header, 4, 16);
        return header;
    }

    private static byte[] EncodeUserPassword(string password, byte[] requestAuthenticator, string sharedSecret)
    {
        var secret = Encoding.UTF8.GetBytes(sharedSecret);
        var plain = Encoding.UTF8.GetBytes(password);
        if (plain.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(password), "RADIUS passwords are limited to 128 bytes.");
        }

        var paddedLength = Math.Max(16, (plain.Length + 15) / 16 * 16);
        var padded = new byte[paddedLength];
        Array.Copy(plain, padded, plain.Length);

        var result = new byte[paddedLength];
        var previous = requestAuthenticator;
        for (var chunk = 0; chunk < paddedLength / 16; chunk++)
        {
            var round = MD5.HashData([.. secret, .. previous]);
            for (var i = 0; i < 16; i++)
            {
                result[chunk * 16 + i] = (byte)(padded[chunk * 16 + i] ^ round[i]);
            }

            previous = result.AsSpan(chunk * 16, 16).ToArray();
        }

        return result;
    }

    private static void WriteAttribute(Stream stream, byte type, byte[] value)
    {
        stream.WriteByte(type);
        stream.WriteByte((byte)(value.Length + 2));
        stream.Write(value);
    }
}
