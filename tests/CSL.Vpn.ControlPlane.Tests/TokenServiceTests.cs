using CSL.Vpn.ControlPlane.Security;
using Xunit;

namespace CSL.Vpn.ControlPlane.Tests;

public sealed class TokenServiceTests
{
    [Fact]
    public void NewToken_ReturnsUrlSafeToken()
    {
        var token = TokenService.NewToken();

        Assert.Equal(43, token.Length); // 32 bytes -> 43 base64url chars without padding
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
    }

    [Fact]
    public void NewToken_GeneratesUniqueTokens()
    {
        var first = TokenService.NewToken();
        var second = TokenService.NewToken();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewToken_RejectsWeakSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TokenService.NewToken(8));
    }

    [Fact]
    public void Hash_IsDeterministicAndSha256Length()
    {
        var token = TokenService.NewToken();

        var first = TokenService.Hash(token);
        var second = TokenService.Hash(token);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_DiffersForDifferentTokens()
    {
        var first = TokenService.Hash(TokenService.NewToken());
        var second = TokenService.Hash(TokenService.NewToken());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void HashMatches_WorksBothWays()
    {
        var token = TokenService.NewToken();
        var hash = TokenService.Hash(token);

        Assert.True(TokenService.HashMatches(token, hash));
        Assert.False(TokenService.HashMatches(TokenService.NewToken(), hash));
    }
}
