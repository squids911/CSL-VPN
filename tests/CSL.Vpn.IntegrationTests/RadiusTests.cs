using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CSL.Vpn.ControlPlane.Security;
using CslVpn.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// End-to-end RADIUS: a device enrolled through the public API must be
/// accepted by the RADIUS facade (which SoftEther delegates VPN logins to),
/// and rejection must follow credential or device revocation.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RadiusTests(ApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Enrolled_device_is_accepted_and_revoked_device_is_rejected()
    {
        using var client = fixture.CreateClient();
        var enroll = await EnrollAsync(client, $"radius-{Guid.NewGuid():N}");

        // Valid credential -> Access-Accept.
        Assert.Equal(
            RadiusPacket.CodeAccessAccept,
            await SendRequestAsync(enroll.DeviceId.ToString(), enroll.Credential));

        // Wrong credential -> Access-Reject.
        Assert.Equal(
            RadiusPacket.CodeAccessReject,
            await SendRequestAsync(enroll.DeviceId.ToString(), "wrong-credential"));

        // Non-GUID username -> Access-Reject.
        Assert.Equal(
            RadiusPacket.CodeAccessReject,
            await SendRequestAsync("not-a-device", enroll.Credential));

        // Revoked device -> Access-Reject.
        using var revoke = new HttpRequestMessage(HttpMethod.Post, $"/admin/devices/{enroll.DeviceId}/revoke");
        revoke.Headers.Add("X-Admin-Key", ApiFixture.AdminKey);
        using var revokeResponse = await client.SendAsync(revoke);
        revokeResponse.EnsureSuccessStatusCode();

        Assert.Equal(
            RadiusPacket.CodeAccessReject,
            await SendRequestAsync(enroll.DeviceId.ToString(), enroll.Credential));
    }

    private async Task<EnrollResponse> EnrollAsync(HttpClient client, string displayName)
    {
        using var codeRequest = new HttpRequestMessage(HttpMethod.Post, "/admin/enrollment-codes");
        codeRequest.Headers.Add("X-Admin-Key", ApiFixture.AdminKey);
        using var codeResponse = await client.SendAsync(codeRequest);
        codeResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await codeResponse.Content.ReadAsStringAsync());
        var code = document.RootElement.GetProperty("code").GetString()!;

        using var enrollResponse = await client.PostAsJsonAsync(
            "/enroll", new EnrollRequest(code, displayName, null), JsonOptions);
        enrollResponse.EnsureSuccessStatusCode();
        return (await enrollResponse.Content.ReadFromJsonAsync<EnrollResponse>(JsonOptions))!;
    }

    private async Task<byte> SendRequestAsync(string username, string password)
    {
        var server = fixture.Services.GetRequiredService<RadiusServer>();
        var endpoint = await WaitForBoundEndpointAsync(server);

        var identifier = (byte)Random.Shared.Next(256);
        var request = RadiusPacket.CreateAccessRequest(identifier, username, password, ApiFixture.RadiusSecret);

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync(request, endpoint);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await udp.ReceiveAsync(timeout.Token);
        Assert.Equal(identifier, result.Buffer[1]);
        return result.Buffer[0];
    }

    private static async Task<IPEndPoint> WaitForBoundEndpointAsync(RadiusServer server)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (server.BoundEndpoint is { } endpoint)
            {
                return endpoint;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("RADIUS server did not bind in time.");
    }
}
