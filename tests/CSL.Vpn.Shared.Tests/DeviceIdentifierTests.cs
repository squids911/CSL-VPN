using CslVpn.Shared;
using Xunit;

namespace CSL.Vpn.Shared.Tests;

public sealed class DeviceIdentifierTests
{
    [Fact]
    public void Create_ReturnsNonEmptyIdentifier()
    {
        var identifier = DeviceIdentifier.Create();

        Assert.NotEqual(Guid.Empty, identifier);
    }

    [Fact]
    public void Create_ReturnsUniqueIdentifiers()
    {
        var first = DeviceIdentifier.Create();
        var second = DeviceIdentifier.Create();

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryParse_AcceptsCanonicalGuid()
    {
        const string value = "7f3a1c5e-9b2d-4e8f-a1c3-5d7b9e2f4a6c";

        var parsed = DeviceIdentifier.TryParse(value, out var identifier);

        Assert.True(parsed);
        Assert.Equal(Guid.Parse(value), identifier);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("7f3a1c5e9b2d4e8fa1c35d7b9e2f4a6c")] // Only the canonical "D" form is accepted.
    public void TryParse_RejectsInvalidOrEmpty(string? value)
    {
        var parsed = DeviceIdentifier.TryParse(value, out var identifier);

        Assert.False(parsed);
        Assert.Equal(Guid.Empty, identifier);
    }
}
