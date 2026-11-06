using CSLVpn.Client.Core;
using Microsoft.Extensions.Logging;

namespace CSLVpn.Service;

public sealed class ManagementEnrollment(
    ManagementApiClient api,
    DeviceIdentityStore identityStore,
    ILogger logger)
{
    public async Task<DeviceIdentity> EnsureEnrolledAsync(
        string? bootstrapToken,
        CancellationToken cancellationToken)
    {
        var identity = identityStore.LoadOrCreate();
        if (!string.IsNullOrWhiteSpace(identity.ServerDeviceId))
            return identity;

        if (string.IsNullOrWhiteSpace(bootstrapToken))
            throw new InvalidOperationException(
                "CSLVPN_BOOTSTRAP_TOKEN is required for the first device enrollment.");

        var result = await api.EnrollAsync(
            bootstrapToken,
            Environment.MachineName,
            identity.Fingerprint,
            identity.DeviceCredential,
            cancellationToken);

        identity = identity with
        {
            ServerDeviceId = result.DeviceId,
            VpnIPv4 = result.VpnIPv4
        };
        identityStore.Save(identity);
        logger.LogInformation(
            "Device enrolled with CSL VPN management plane; assigned address {VpnIPv4}",
            identity.VpnIPv4);
        return identity;
    }
}
