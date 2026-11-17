using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CslVpn.Shared.Contracts;

namespace CSL.Vpn.WindowsService;

/// <summary>Typed result of one control-plane call.</summary>
public sealed record ApiCallResult<T>(bool Success, bool Unauthorized, string? Error, T? Value)
{
    public static ApiCallResult<T> Ok(T value) => new(true, false, null, value);

    public static ApiCallResult<T> Fail(string error, bool unauthorized = false) =>
        new(false, unauthorized, error, default);
}

/// <summary>HTTP client for the CSL VPN control plane: enrollment, heartbeat, status, disconnect.</summary>
public sealed class ControlPlaneClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ApiCallResult<EnrollResponse>> EnrollAsync(
        string code, string displayName, Guid deviceId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            "/enroll", new EnrollRequest(code, displayName, deviceId), JsonOptions, cancellationToken);
        return await ReadAsync<EnrollResponse>(response, cancellationToken);
    }

    public async Task<ApiCallResult<HeartbeatResponse>> HeartbeatAsync(
        string credential, CancellationToken cancellationToken)
    {
        using var request = BearerRequest(HttpMethod.Post, "/session/heartbeat", credential);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadAsync<HeartbeatResponse>(response, cancellationToken);
    }

    public async Task<ApiCallResult<DeviceStatusResponse>> GetStatusAsync(
        string credential, CancellationToken cancellationToken)
    {
        using var request = BearerRequest(HttpMethod.Get, "/device/status", credential);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return await ReadAsync<DeviceStatusResponse>(response, cancellationToken);
    }

    public async Task<bool> DisconnectAsync(string credential, CancellationToken cancellationToken)
    {
        using var request = BearerRequest(HttpMethod.Post, "/session/disconnect", credential);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static HttpRequestMessage BearerRequest(HttpMethod method, string path, string credential)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }

    private static async Task<ApiCallResult<T>> ReadAsync<T>(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            return value is null ? ApiCallResult<T>.Fail("empty_response") : ApiCallResult<T>.Ok(value);
        }

        var error = $"http_{(int)response.StatusCode}";
        try
        {
            var errorPayload = await response.Content.ReadFromJsonAsync<ErrorResponse>(JsonOptions, cancellationToken);
            if (errorPayload?.Error is { Length: > 0 } code)
            {
                error = code;
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body; keep the HTTP status fallback.
        }

        return ApiCallResult<T>.Fail(
            error, response.StatusCode == System.Net.HttpStatusCode.Unauthorized);
    }
}
