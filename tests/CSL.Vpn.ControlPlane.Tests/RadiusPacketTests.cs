using System.Security.Cryptography;
using System.Text;
using CSL.Vpn.ControlPlane.Security;
using Xunit;

namespace CSL.Vpn.ControlPlane.Tests;

public sealed class RadiusPacketTests
{
    private const string Secret = "unit-test-secret";

    [Theory]
    [InlineData("a")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdefg")]
    [InlineData("токен-с-юникодом-1234567890")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_ABCDEFGHIJKLMNOPQRSTUVWXYZ")]
    public void Access_request_round_trips_username_and_password(string password)
    {
        var packet = RadiusPacket.CreateAccessRequest(7, "user-1", password, Secret);

        Assert.True(RadiusPacket.TryParseAccessRequest(packet, out var parsed));
        Assert.Equal(7, parsed.Identifier);
        Assert.Equal("user-1", parsed.GetAttributeString(RadiusPacket.AttrUserName));

        var decoded = RadiusPacket.DecodeUserPassword(
            parsed.GetAttribute(RadiusPacket.AttrUserPassword)!, parsed.Authenticator, Secret);
        Assert.Equal(password, decoded);
    }

    [Fact]
    public void Wrong_secret_yields_garbage_password()
    {
        var packet = RadiusPacket.CreateAccessRequest(1, "user", "correct-horse", Secret);
        Assert.True(RadiusPacket.TryParseAccessRequest(packet, out var parsed));
        var decoded = RadiusPacket.DecodeUserPassword(
            parsed.GetAttribute(RadiusPacket.AttrUserPassword)!, parsed.Authenticator, "other-secret");
        Assert.NotEqual("correct-horse", decoded);
    }

    [Fact]
    public void Rejects_malformed_datagrams()
    {
        Assert.False(RadiusPacket.TryParseAccessRequest(Array.Empty<byte>(), out _));
        Assert.False(RadiusPacket.TryParseAccessRequest(new byte[19], out _));

        var notARequest = RadiusPacket.CreateAccessRequest(1, "u", "p", Secret);
        notARequest[0] = RadiusPacket.CodeAccessAccept;
        Assert.False(RadiusPacket.TryParseAccessRequest(notARequest, out _));

        var truncated = RadiusPacket.CreateAccessRequest(1, "u", "p", Secret);
        Assert.False(RadiusPacket.TryParseAccessRequest(truncated[..^1], out _));
    }

    [Fact]
    public void Response_authenticator_matches_rfc2865()
    {
        var request = RadiusPacket.CreateAccessRequest(42, "user", "pass", Secret);
        Assert.True(RadiusPacket.TryParseAccessRequest(request, out var parsed));

        var response = RadiusPacket.BuildResponse(RadiusPacket.CodeAccessAccept, parsed, Secret);

        Assert.Equal(RadiusPacket.CodeAccessAccept, response[0]);
        Assert.Equal(42, response[1]);
        Assert.Equal(20, (response[2] << 8) | response[3]);

        // ResponseAuth = MD5(Code + ID + Length + RequestAuth + Secret)
        var expected = MD5.HashData([
            response[0], response[1], response[2], response[3],
            .. parsed.Authenticator,
            .. Encoding.UTF8.GetBytes(Secret),
        ]);
        Assert.Equal(expected, response.AsSpan(4, 16).ToArray());
    }
}
