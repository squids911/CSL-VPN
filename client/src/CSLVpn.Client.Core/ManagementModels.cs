using System.Text.Json.Serialization;

namespace CSLVpn.Client.Core;

public sealed record EnrollmentResult(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("vpn_ipv4")] string VpnIPv4,
    [property: JsonPropertyName("status")] string Status);

public sealed record DeviceProfile(
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("vpn_ipv4")] string VpnIPv4,
    [property: JsonPropertyName("vpn_prefix")] string VpnPrefix,
    [property: JsonPropertyName("site_id")] string? SiteId,
    [property: JsonPropertyName("status")] string Status);

public sealed record SessionLease(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("session_token")] string SessionToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
