using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CslVpn.Shared.Contracts;
using Npgsql;
using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// The SoftEther SecureNAT gateway occupies 10.255.0.1 inside the client pool
/// range. Migration 0002 must keep it out of the allocatable pool forever.
/// </summary>
public sealed class AddressPoolTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Gateway_address_is_not_allocatable()
    {
        await using var dataSource = NpgsqlDataSource.Create(ApiFixture.ConnectionString);

        await using var gateway = dataSource.CreateCommand(
            "SELECT count(*) FROM address_pool WHERE address = '10.255.0.1'");
        Assert.Equal(0L, (long)(await gateway.ExecuteScalarAsync())!);

        // 1022 addresses from 0001 (10.255.0.1 - 10.255.3.254) minus the gateway.
        await using var total = dataSource.CreateCommand("SELECT count(*) FROM address_pool");
        Assert.Equal(1021L, (long)(await total.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Enrollment_never_assigns_the_gateway_address()
    {
        using var client = fixture.CreateClient();

        using var codeRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/enrollment-codes");
        codeRequest.Headers.Add("X-Admin-Key", ApiFixture.AdminKey);
        using var codeResponse = await client.SendAsync(codeRequest);
        codeResponse.EnsureSuccessStatusCode();
        using var codeDocument = JsonDocument.Parse(await codeResponse.Content.ReadAsStringAsync());
        var code = codeDocument.RootElement.GetProperty("code").GetString()!;

        using var enrollResponse = await client.PostAsJsonAsync(
            "/enroll", new EnrollRequest(code, "pool-test", null));
        enrollResponse.EnsureSuccessStatusCode();
        var enroll = await enrollResponse.Content.ReadFromJsonAsync<EnrollResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(enroll);
        Assert.NotEqual("10.255.0.1", enroll.Address);
        Assert.StartsWith("10.255.", enroll.Address);
    }
}

/// <summary>
/// /enroll is limited to 30 requests per minute per client IP (partitioned by
/// the first X-Forwarded-For hop set by the reverse proxy). A fresh partition
/// key isolates this test from every other test's traffic.
/// </summary>
public sealed class RateLimitingTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Enroll_is_rate_limited_per_client_ip()
    {
        using var client = fixture.CreateClient();
        var partitionKey = $"test-{Guid.NewGuid():N}";

        HttpStatusCode lastStatus = default;
        var tooManyRequests = 0;
        string? rejectionBody = null;

        for (var attempt = 0; attempt < 31; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/enroll");
            request.Headers.Add("X-Forwarded-For", partitionKey);
            request.Content = JsonContent.Create(new EnrollRequest("wrong-code", "rl", null));
            using var response = await client.SendAsync(request);

            lastStatus = response.StatusCode;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                tooManyRequests++;
                rejectionBody = await response.Content.ReadAsStringAsync();
            }
        }

        Assert.Equal(1, tooManyRequests);
        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
        Assert.NotNull(rejectionBody);
        Assert.Contains("rate_limited", rejectionBody);
    }

    [Fact]
    public async Task Heartbeat_is_rate_limited_per_client_ip()
    {
        using var client = fixture.CreateClient();
        var partitionKey = $"test-{Guid.NewGuid():N}";

        var tooManyRequests = 0;
        for (var attempt = 0; attempt < 121; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/session/heartbeat");
            request.Headers.Add("X-Forwarded-For", partitionKey);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong");
            using var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                tooManyRequests++;
            }
        }

        Assert.Equal(1, tooManyRequests);
    }
}
