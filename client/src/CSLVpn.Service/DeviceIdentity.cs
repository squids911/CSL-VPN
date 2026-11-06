using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSLVpn.Client.Core;

namespace CSLVpn.Service;

public sealed record DeviceIdentity(
    Guid PersistentDeviceId,
    string DeviceCredential,
    string? ServerDeviceId = null,
    string? VpnIPv4 = null)
{
    public string Fingerprint => DeviceSecret.CreateFingerprint(PersistentDeviceId);
}

public sealed class DeviceIdentityStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CSL-VPN-Device-Identity-v1");
    private readonly string _path;

    public DeviceIdentityStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CSL VPN",
            "device.identity");
    }

    public DeviceIdentity LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<DeviceIdentity>(clearBytes)
                ?? throw new InvalidDataException("The protected CSL VPN identity file is empty.");
        }

        var identity = new DeviceIdentity(
            Guid.NewGuid(),
            DeviceSecret.CreateCredential());
        Save(identity);
        return identity;
    }

    public void Save(DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Identity file path has no parent directory.");
        Directory.CreateDirectory(directory);

        var clearBytes = JsonSerializer.SerializeToUtf8Bytes(identity);
        var protectedBytes = ProtectedData.Protect(
            clearBytes, Entropy, DataProtectionScope.CurrentUser);
        var temporaryPath = _path + ".tmp";

        try
        {
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            CryptographicOperations.ZeroMemory(clearBytes);
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}
