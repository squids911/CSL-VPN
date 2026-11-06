using System.Security.Cryptography;

namespace CSLVpn.Client.Core;

public static class DeviceSecret
{
    public static string CreateCredential()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    public static string CreateFingerprint(Guid persistentDeviceId)
    {
        var digest = SHA256.HashData(persistentDeviceId.ToByteArray());
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
