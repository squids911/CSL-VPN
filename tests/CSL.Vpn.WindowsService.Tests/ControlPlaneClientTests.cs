using System.Net;
using CSL.Vpn.WindowsService;
using Xunit;

namespace CSL.Vpn.WindowsService.Tests;

public sealed class ControlPlaneClientTests
{
    private static ControlPlaneClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> handler) =>
        new(new HttpClient(new StubHandler(handler)) { BaseAddress = new Uri("https://test.local") });

    [Fact]
    public async Task Enroll_ParsesSuccessResponse()
    {
        var deviceId = Guid.NewGuid();
        var client = CreateClient(_ => Json("""
            {"deviceId":"GUID","displayName":"host","address":"10.255.0.5","credential":"tok"}
            """.Replace("GUID", deviceId.ToString())));

        var result = await client.EnrollAsync("code", "host", deviceId, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(deviceId, result.Value.DeviceId);
        Assert.Equal("10.255.0.5", result.Value.Address);
        Assert.Equal("tok", result.Value.Credential);
    }

    [Fact]
    public async Task Enroll_SurfacesServerErrorCode()
    {
        var client = CreateClient(_ => Json("""{"error":"code_consumed"}""", HttpStatusCode.BadRequest));

        var result = await client.EnrollAsync("code", "host", Guid.NewGuid(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("code_consumed", result.Error);
        Assert.False(result.Unauthorized);
    }

    [Fact]
    public async Task Heartbeat_ParsesSession()
    {
        var sessionId = Guid.NewGuid();
        var client = CreateClient(_ => Json("""
            {"sessionId":"GUID","startedAt":"2026-11-17T00:00:00+00:00","lastHeartbeat":"2026-11-17T00:01:00+00:00","address":"10.255.0.5","status":"active"}
            """.Replace("GUID", sessionId.ToString())));

        var result = await client.HeartbeatAsync("tok", CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Value);
        Assert.Equal(sessionId, result.Value.SessionId);
        Assert.Equal("active", result.Value.Status);
    }

    [Fact]
    public async Task Heartbeat_UnauthorizedIsFlagged()
    {
        var client = CreateClient(_ => Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized));

        var result = await client.HeartbeatAsync("tok", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.Unauthorized);
        Assert.Equal("unauthorized", result.Error);
    }

    [Fact]
    public async Task NonJsonErrorFallsBackToHttpStatus()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>bad gateway</html>"),
        });

        var result = await client.HeartbeatAsync("tok", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("http_502", result.Error);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
