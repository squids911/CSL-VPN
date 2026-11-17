using System.Security.AccessControl;
using System.Text.Json;

namespace CSL.Vpn.WindowsService;

public sealed record ClientIdentity(Guid DeviceId, string Credential);

/// <summary>
/// Persists the device identity under ProgramData. The device GUID lives in
/// device-id.txt and survives credential rotation and reinstalls; the
/// credential lives in identity.json with ACLs restricted to SYSTEM and
/// Administrators (best effort — a failure is logged, not fatal).
/// </summary>
public sealed class ClientIdentityStore(ILogger<ClientIdentityStore> logger, IConfiguration configuration)
{
    private string IdentityFile =>
        configuration["ControlPlane:IdentityFile"] ?? @"C:\ProgramData\CSL VPN\identity.json";

    private string DirectoryPath => Path.GetDirectoryName(IdentityFile)!;

    private string DeviceIdFile => Path.Combine(DirectoryPath, "device-id.txt");

    public ClientIdentity? TryLoad()
    {
        try
        {
            if (!File.Exists(IdentityFile))
            {
                return null;
            }

            var identity = JsonSerializer.Deserialize<ClientIdentity>(File.ReadAllText(IdentityFile));
            return identity is { Credential.Length: > 0 } ? identity : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the stored device identity; will enroll again.");
            return null;
        }
    }

    public Guid LoadOrCreateDeviceId()
    {
        var existing = TryLoad();
        if (existing is not null)
        {
            return existing.DeviceId;
        }

        try
        {
            if (File.Exists(DeviceIdFile)
                && Guid.TryParse(File.ReadAllText(DeviceIdFile).Trim(), out var persisted)
                && persisted != Guid.Empty)
            {
                return persisted;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the persisted device id; a new one will be created.");
        }

        var created = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(DeviceIdFile, created.ToString("D"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not persist the device id; it will change on restart.");
        }

        return created;
    }

    public void Save(ClientIdentity identity)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(IdentityFile, JsonSerializer.Serialize(identity));
        File.WriteAllText(DeviceIdFile, identity.DeviceId.ToString("D"));
        TryRestrictAcl(IdentityFile);
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(IdentityFile))
            {
                File.Delete(IdentityFile);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not delete the identity file.");
        }
    }

    private void TryRestrictAcl(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var security = fileInfo.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.ResetAccessRule(new FileSystemAccessRule(
                "SYSTEM", FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                "Administrators", FileSystemRights.FullControl, AccessControlType.Allow));
            fileInfo.SetAccessControl(security);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not restrict ACLs on {Path}.", path);
        }
    }
}
