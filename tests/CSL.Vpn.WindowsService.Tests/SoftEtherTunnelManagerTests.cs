using CSL.Vpn.WindowsService.Tunnel;
using Xunit;

namespace CSL.Vpn.WindowsService.Tests;

public sealed class SoftEtherTunnelManagerTests
{
    [Fact]
    public void Account_create_arguments_carry_server_hub_username_and_nic()
    {
        var deviceId = Guid.Parse("83e1d3c4-76b8-4de8-a3e8-3912b10e4845");
        var args = SoftEtherTunnelManager.BuildAccountCreateArguments("CSLVPN", "v.csl.by:5555", "CSL", deviceId, "CSLVPN");

        Assert.Contains("AccountCreate CSLVPN", args);
        Assert.Contains("/SERVER:v.csl.by:5555", args);
        Assert.Contains("/HUB:CSL", args);
        Assert.Contains("/USERNAME:83e1d3c4-76b8-4de8-a3e8-3912b10e4845", args);
        Assert.Contains("/NICNAME:CSLVPN", args);
    }

    [Fact]
    public void Password_arguments_request_radius_auth()
    {
        var args = SoftEtherTunnelManager.BuildPasswordArguments("CSLVPN", "secret-token");
        Assert.Contains("/PASSWORD:secret-token", args);
        Assert.Contains("/TYPE:radius", args);
    }

    [Fact]
    public void Netsh_arguments_quote_adapter_and_apply_static_address()
    {
        var args = SoftEtherTunnelManager.BuildNetshArguments(
            "VPN Client Adapter - CSLVPN", "10.255.0.42", "255.255.252.0", "10.255.0.1");
        Assert.Equal(
            "interface ip set address name=\"VPN Client Adapter - CSLVPN\" static 10.255.0.42 255.255.252.0 10.255.0.1 1",
            args);
    }

    [Fact]
    public void Nic_and_account_list_parsing_is_case_insensitive()
    {
        const string nicList = """
            Item                           |Value
            -------------------------------+---------------------------------------
            Virtual Network Adapter Name   |VPN Client Adapter - CSLVPN
            """;
        Assert.True(SoftEtherOutput.ListContains(nicList, "cslvpn"));
        Assert.False(SoftEtherOutput.ListContains(nicList, "other"));
    }

    [Fact]
    public void Connection_status_detection()
    {
        Assert.True(SoftEtherOutput.IsConnected("Session Status: Connection Completed (Session Established)"));
        Assert.False(SoftEtherOutput.IsConnected("Session Status: Connecting"));
        Assert.False(SoftEtherOutput.IsConnected("Session Status: Connection Failed"));
    }
}
