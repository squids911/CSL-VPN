using System.Security.Cryptography;
using System.Text;

namespace CSL.Vpn.ControlPlane.AdminUi;

/// <summary>
/// Stateless cookie sessions for the admin UI. The cookie carries an expiry
/// stamp plus an HMAC-SHA256 keyed with the admin API key — the key itself is
/// never stored in the browser. The CSRF token is an HMAC over the cookie
/// value, so it is bound to the session and needs no server state.
/// </summary>
public static class AdminUiAuth
{
    public const string CookieName = "csl_admin_ui";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    private const string SessionScope = "csl-admin-ui-v1";
    private const string CsrfScope = "csl-admin-csrf";

    public static string IssueCookieValue(string adminKey, DateTimeOffset now)
    {
        var expiry = now.Add(SessionLifetime).ToUnixTimeSeconds();
        return $"{expiry}.{Sign(adminKey, $"{SessionScope}|{expiry}")}";
    }

    public static bool IsValidCookie(string? cookieValue, string adminKey, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookieValue))
        {
            return false;
        }

        var dot = cookieValue.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || !long.TryParse(cookieValue[..dot], out var expiry))
        {
            return false;
        }

        if (expiry <= now.ToUnixTimeSeconds())
        {
            return false;
        }

        var expected = $"{expiry}.{Sign(adminKey, $"{SessionScope}|{expiry}")}";
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(cookieValue), Encoding.UTF8.GetBytes(expected));
    }

    public static string IssueCsrfToken(string cookieValue, string adminKey) =>
        Sign(adminKey, $"{CsrfScope}|{cookieValue}");

    public static bool IsValidCsrfToken(string? token, string cookieValue, string adminKey)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var expected = IssueCsrfToken(cookieValue, adminKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    private static string Sign(string key, string message)
    {
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(message));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
