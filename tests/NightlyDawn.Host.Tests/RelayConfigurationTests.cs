using NightlyDawn.Host;
using Xunit;

namespace NightlyDawn.Host.Tests;

public class RelayConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetOrBlank_UsesTheDefaults(string? value)
    {
        var relays = RelayConfiguration.Parse(value);

        Assert.Equal(["wss://relay.damus.io/", "wss://nos.lol/"], relays.Select(r => r.Value));
    }

    [Fact]
    public void CommaSeparated_IsParsedTrimmedAndDeduplicated()
    {
        var relays = RelayConfiguration.Parse(" wss://a.example , wss://b.example/,wss://a.example/ ,, ");

        Assert.Equal(["wss://a.example/", "wss://b.example/"], relays.Select(r => r.Value));
    }

    [Theory]
    [InlineData("ws://plain.example")]
    [InlineData("https://not-a-relay.example")]
    [InlineData("wss://ok.example,nonsense")]
    [InlineData(",")]
    public void InsecureOrJunk_IsRejectedBeforeAnySocketOpens(string value)
    {
        Assert.Throws<ArgumentException>(() => RelayConfiguration.Parse(value));
    }
}
