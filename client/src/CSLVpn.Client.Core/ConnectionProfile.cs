namespace CSLVpn.Client.Core;

public enum VpnProtocol
{
    SoftEther,
    WireGuard
}

public enum TransportMode
{
    Automatic,
    Udp,
    Tcp
}

public sealed record ConnectionProfile(
    Uri ManagementEndpoint,
    VpnProtocol Protocol,
    TransportMode Transport,
    string DeviceName);
