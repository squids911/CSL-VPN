using System.Security.Cryptography;
using System.Text;

namespace CSL.Vpn.ControlPlane.Security;

/// <summary>
/// Generates opaque bearer tokens and enrollment codes, and hashes them for
/// storage. Raw values are shown to the caller once and never stored.
/// </summary>
public static class TokenService
{
    public static string NewToken(int sizeBytes = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizeBytes, 16);
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(sizeBytes));
    }

    public static byte[] Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public static bool HashMatches(string token, byte[] expectedHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        ArgumentNullException.ThrowIfNull(expectedHash);
        return CryptographicOperations.FixedTimeEquals(Hash(token), expectedHash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
