using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace CSLVpn.Client.Core;

public sealed class ManagementApiClient
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public ManagementApiClient(HttpClient http, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("CSL VPN management endpoint must use HTTPS.", nameof(endpoint));

        _http = http;
        _endpoint = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);
    }

    public async Task<EnrollmentResult> EnrollAsync(
        string bootstrapToken,
        string deviceName,
        string deviceFingerprint,
        string deviceCredential,
        CancellationToken cancellationToken = default)
    {
        var request = new EnrollmentRequest(bootstrapToken, deviceName, deviceFingerprint, deviceCredential);
        using var response = await _http.PostAsJsonAsync(
            new Uri(_endpoint, "api/v1/enroll"), request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<EnrollmentResult>(cancellationToken)
            ?? throw new InvalidDataException("The management API returned an empty enrollment response.");
    }

    public Task<DeviceProfile> GetDeviceProfileAsync(
        string deviceCredential,
        CancellationToken cancellationToken = default) =>
        SendAuthorizedAsync<DeviceProfile>(
            HttpMethod.Get, "api/v1/device", deviceCredential, null, cancellationToken);

    public Task<SessionLease> StartSessionAsync(
        string deviceCredential,
        VpnProtocol protocol,
        TransportMode transport,
        CancellationToken cancellationToken = default)
    {
        if (transport == TransportMode.Automatic)
            throw new ArgumentException("Automatic transport must be resolved to a tested transport before requesting a session.", nameof(transport));

        var request = new StartSessionRequest(
            protocol == VpnProtocol.SoftEther ? "softether" : "wireguard",
            transport == TransportMode.Udp ? "udp" : "tcp");

        return SendAuthorizedAsync<SessionLease>(
            HttpMethod.Post, "api/v1/sessions", deviceCredential, request, cancellationToken);
    }

    public async Task SendHeartbeatAsync(
        string deviceCredential,
        string sessionToken,
        CancellationToken cancellationToken = default)
    {
        _ = await SendAuthorizedAsync<HeartbeatResult>(
            HttpMethod.Post,
            "api/v1/heartbeat",
            deviceCredential,
            new HeartbeatRequest(sessionToken),
            cancellationToken);
    }

    private async Task<T> SendAuthorizedAsync<T>(
        HttpMethod method,
        string path,
        string deviceCredential,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceCredential);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidDataException($"The management API returned an empty response for {path}.");
    }

    private sealed record EnrollmentRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("bootstrap_token")] string BootstrapToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("device_name")] string DeviceName,
        [property: System.Text.Json.Serialization.JsonPropertyName("device_fingerprint")] string DeviceFingerprint,
        [property: System.Text.Json.Serialization.JsonPropertyName("device_credential")] string DeviceCredential);

    private sealed record StartSessionRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("protocol")] string Protocol,
        [property: System.Text.Json.Serialization.JsonPropertyName("transport")] string Transport);

    private sealed record HeartbeatRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("session_token")] string SessionToken);

    private sealed record HeartbeatResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status);
}
