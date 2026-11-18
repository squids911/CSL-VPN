using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CslVpn.Shared.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace CSL.Vpn.IntegrationTests;

/// <summary>
/// The operator UI at /admin/ui: cookie login, CSRF enforcement, code
/// creation, revocation — verified end to end against the real API and DB.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AdminUiTests(ApiFixture fixture)
{
    private static readonly Regex CsrfField =
        new("name=\"csrf\" value=\"([^\"]+)\"", RegexOptions.Compiled);

    [Fact]
    public async Task Full_ui_flow_login_create_code_revoke()
    {
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // No cookie -> redirect to the login page.
        using (var anonymous = await client.GetAsync("/admin/ui/"))
        {
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.Equal("/admin/ui/login", anonymous.Headers.Location?.ToString());
        }

        // Wrong key -> 401, no session cookie.
        using (var wrong = await client.PostAsync("/admin/ui/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = "wrong" })))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.False(wrong.Headers.Contains("Set-Cookie"));
        }

        // Right key -> session cookie + redirect to the dashboard.
        string cookie;
        using (var login = await client.PostAsync("/admin/ui/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = ApiFixture.AdminKey })))
        {
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            Assert.StartsWith("csl_admin_ui=", cookie);
        }

        // Dashboard with the cookie -> 200, contains the CSRF token.
        var dashboard = await GetStringAsync(client, "/admin/ui/", cookie);
        Assert.Contains("Devices", dashboard);
        var csrf = CsrfField.Match(dashboard).Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(csrf));

        // Creating a code without CSRF -> 400.
        using (var noCsrf = await client.SendAsync(WithCookie(
            new HttpRequestMessage(HttpMethod.Post, "/admin/ui/codes")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["expiresInMinutes"] = "30" }),
            }, cookie)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        }

        // With CSRF -> the code is shown once on the resulting page...
        string code;
        using (var created = await client.SendAsync(WithCookie(
            new HttpRequestMessage(HttpMethod.Post, "/admin/ui/codes")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["expiresInMinutes"] = "30",
                    ["csrf"] = csrf,
                }),
            }, cookie)))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var html = await created.Content.ReadAsStringAsync();
            Assert.Contains("shown once", html);
            code = Regex.Match(html, "class=\"code\">([^<]+)<").Groups[1].Value;
            Assert.Equal(22, code.Length);
        }

        // ...and the code actually works on the public endpoint.
        var enrollResponse = await client.PostAsJsonAsync(
            "/enroll", new EnrollRequest(code, "ui-test", null));
        enrollResponse.EnsureSuccessStatusCode();
        var enroll = await enrollResponse.Content.ReadFromJsonAsync<EnrollResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(enroll);

        // Revoke the device through the UI.
        var dashboard2 = await GetStringAsync(client, "/admin/ui/", cookie);
        var csrf2 = CsrfField.Match(dashboard2).Groups[1].Value;
        using (var revoke = await client.SendAsync(WithCookie(
            new HttpRequestMessage(HttpMethod.Post, $"/admin/ui/devices/{enroll.DeviceId}/revoke")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["csrf"] = csrf2 }),
            }, cookie)))
        {
            Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        }

        // The revoked device can no longer heartbeat.
        using var heartbeat = new HttpRequestMessage(HttpMethod.Post, "/session/heartbeat");
        heartbeat.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", enroll.Credential);
        using var heartbeatResponse = await client.SendAsync(heartbeat);
        Assert.Equal(HttpStatusCode.Unauthorized, heartbeatResponse.StatusCode);

        // A tampered cookie is rejected.
        using var tampered = await client.SendAsync(
            WithCookie(new HttpRequestMessage(HttpMethod.Get, "/admin/ui/"), cookie[..^2] + "xx"));
        Assert.Equal(HttpStatusCode.Redirect, tampered.StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited()
    {
        using var client = fixture.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var partitionKey = $"ui-test-{Guid.NewGuid():N}";

        var tooMany = 0;
        for (var attempt = 0; attempt < 11; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/ui/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = "wrong" }),
            };
            request.Headers.Add("X-Forwarded-For", partitionKey);
            using var response = await client.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                tooMany++;
            }
        }

        Assert.Equal(1, tooMany);
    }

    private static HttpRequestMessage WithCookie(HttpRequestMessage request, string cookie)
    {
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string path, string cookie)
    {
        using var response = await client.SendAsync(WithCookie(new HttpRequestMessage(HttpMethod.Get, path), cookie));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
