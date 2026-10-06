using System.Reflection;
using Xunit;

namespace NightlyDawn.Core.Tests;

public class ContractTests
{
    [Fact]
    public void FilterAst_RoundTripsThroughRecordEquality()
    {
        var ast = new FilterAst(
            new FilterSource(FilterSourceKind.User, "npub1example"),
            new FilterAnd(
                new FilterComparison("user.name", FilterComparisonOperator.Contains, "x"),
                new FilterNot(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"))));

        var sameShape = new FilterAst(
            new FilterSource(FilterSourceKind.User, "npub1example"),
            new FilterAnd(
                new FilterComparison("user.name", FilterComparisonOperator.Contains, "x"),
                new FilterNot(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"))));

        Assert.Equal(ast, sameShape);
    }

    [Fact]
    public void CompiledFilter_AllowsNoLocalPredicate_WhenRelayFilterIsSufficient()
    {
        var compiled = new CompiledFilter(new NostrFilter(Kinds: [1], Authors: ["abc"]));

        Assert.Null(compiled.LocalPredicate);
        Assert.Equal(1, compiled.RelayFilter.Kinds?.Single());
    }

    [Fact]
    public void PublishResult_AnyAccepted_IsTrue_WhenAtLeastOneRelayAccepted()
    {
        var result = new PublishResult([
            new RelayPublishOutcome(RelayUrl.Parse("wss://a.example"), Accepted: false, Reason: "rate-limited"),
            new RelayPublishOutcome(RelayUrl.Parse("wss://b.example"), Accepted: true),
        ]);

        Assert.True(result.AnyAccepted);
    }

    [Fact]
    public void PublishResult_AnyAccepted_IsFalse_WhenZeroRelaysAccepted()
    {
        var result = new PublishResult([
            new RelayPublishOutcome(RelayUrl.Parse("wss://a.example"), Accepted: false, Reason: "rate-limited"),
        ]);

        Assert.False(result.AnyAccepted);

        var exception = new EventPublishException(result);
        Assert.Same(result, exception.Result);
    }

    [Fact]
    public void RelayUrl_RejectsPlaintextWebsocket_ByDefault()
    {
        Assert.Throws<ArgumentException>(() => RelayUrl.Parse("ws://relay.example"));
    }

    [Fact]
    public void RelayUrl_AllowsPlaintextWebsocket_WhenDevelopmentOptInIsSet()
    {
        var relay = RelayUrl.Parse("ws://localhost:4869", allowInsecureForDevelopment: true);

        Assert.Equal("ws://localhost:4869", relay.ToString());
    }

    [Fact]
    public void RelayUrl_RejectsEmbeddedControlCharacters()
    {
        // A bare StartsWith("wss://") check would have let this through (B12).
        Assert.Throws<ArgumentException>(() => RelayUrl.Parse("wss://relay.example\r\nSec-Fetch: fake"));
    }

    [Fact]
    public void RelayUrl_HasNoPublicParameterlessConstructor()
    {
        var publicConstructors = typeof(RelayUrl).GetConstructors();

        Assert.Empty(publicConstructors);
    }

    [Fact]
    public void Tab_HoldsOrderedTimelineColumns()
    {
        var home = new Timeline("home", "Home", "from home");
        var mentions = new Timeline("mentions", "Mentions", "from mentions");

        var tab = new Tab("main", "Main", [home, mentions]);

        Assert.Equal(["home", "mentions"], tab.Columns.Select(c => c.Id));
    }
}
