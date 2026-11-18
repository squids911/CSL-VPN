using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CslVpn.Shared.Contracts;
using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// Full lifecycle against the real API and a real PostgreSQL:
/// code issuance, enrollment, single-session heartbeat, revocation, cleanup.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EnrollmentFlowTests(ApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Full_enrollment_lifecycle()
    {
        using var client = fixture.CreateClient();

        using (var denied = await client.PostAsync("/admin/enrollment-codes", content: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }

        var code = await CreateCodeAsync(client);
        Assert.Equal(22, code.Length);

        using (var badEnroll = await client.PostAsJsonAsync(
            "/enroll", new EnrollRequest("wrong-code", "bad", null), JsonOptions))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badEnroll.StatusCode);
        }

        var deviceId = Guid.NewGuid();
        var enroll = await PostAsync<EnrollResponse>(
            client, "/enroll", new EnrollRequest(code, "integration-test", deviceId));
        Assert.Equal(deviceId, enroll.DeviceId);
        Assert.StartsWith("10.255.", enroll.Address);
        Assert.False(string.IsNullOrWhiteSpace(enroll.Credential));

        using (var replay = await client.PostAsJsonAsync(
            "/enroll", new EnrollRequest(code, "replay", null), JsonOptions))
        {
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        }

        var first = await PostWithBearerAsync<HeartbeatResponse>(client, "/session/heartbeat", enroll.Credential);
        var second = await PostWithBearerAsync<HeartbeatResponse>(client, "/session/heartbeat", enroll.Credential);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(enroll.Address, second.Address);

        using var wrongCredential = await client.SendAsync(
            BearerRequest(HttpMethod.Post, "/session/heartbeat", "wrong-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCredential.StatusCode);

        using var revoke = await client.SendAsync(
            AdminRequest(HttpMethod.Post, $"/admin/devices/{deviceId}/revoke"));
        Assert.True(revoke.IsSuccessStatusCode);

        using var afterRevoke = await client.SendAsync(
            BearerRequest(HttpMethod.Post, "/session/heartbeat", enroll.Credential));
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevoke.StatusCode);
    }

    [Fact]
    public async Task Two_devices_get_distinct_addresses()
    {
        using var client = fixture.CreateClient();

        var first = await PostAsync<EnrollResponse>(
            client, "/enroll", new EnrollRequest(await CreateCodeAsync(client), "device-a", null));
        var second = await PostAsync<EnrollResponse>(
            client, "/enroll", new EnrollRequest(await CreateCodeAsync(client), "device-b", null));

        Assert.NotEqual(first.Address, second.Address);
    }

    [Fact]
    public async Task Reenroll_with_preserved_identity_keeps_address()
    {
        using var client = fixture.CreateClient();
        var deviceId = Guid.NewGuid();

        var initial = await PostAsync<EnrollResponse>(
            client, "/enroll", new EnrollRequest(await CreateCodeAsync(client), "device-c", deviceId));
        var renewed = await PostAsync<EnrollResponse>(
            client, "/enroll", new EnrollRequest(await CreateCodeAsync(client), "device-c", deviceId));

        Assert.Equal(initial.Address, renewed.Address);
        Assert.NotEqual(initial.Credential, renewed.Credential);

        using var oldCredential = await client.SendAsync(
            BearerRequest(HttpMethod.Post, "/session/heartbeat", initial.Credential));
        Assert.Equal(HttpStatusCode.Unauthorized, oldCredential.StatusCode);
    }

    private async Task<string> CreateCodeAsync(HttpClient client)
    {
        using var response = await client.SendAsync(AdminRequest(HttpMethod.Post, "/admin/enrollment-codes"));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body, JsonOptions);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return value ?? throw new InvalidOperationException("Empty response body.");
    }

    private static async Task<T> PostWithBearerAsync<T>(HttpClient client, string path, string credential)
    {
        using var response = await client.SendAsync(BearerRequest(HttpMethod.Post, path, credential));
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return value ?? throw new InvalidOperationException("Empty response body.");
    }

    private static HttpRequestMessage AdminRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", ApiFixture.AdminKey);
        return request;
    }

    private static HttpRequestMessage BearerRequest(HttpMethod method, string path, string credential)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return request;
    }
}
