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
    public void EventPublishException_FormatsRelayAndReasonIntoMessage()
    {
        var exception = new EventPublishException("wss://relay.example", "rate-limited");

        Assert.Equal("wss://relay.example", exception.RelayUrl);
        Assert.Equal("rate-limited", exception.Reason);
        Assert.Contains("wss://relay.example", exception.Message);
        Assert.Contains("rate-limited", exception.Message);
    }

    [Fact]
    public void Tab_HoldsOrderedTimelineColumns()
    {
        var home = new Timeline("home", "Home", new FilterAst(new FilterSource(FilterSourceKind.Home)));
        var mentions = new Timeline("mentions", "Mentions", new FilterAst(new FilterSource(FilterSourceKind.Mentions)));

        var tab = new Tab("main", "Main", [home, mentions]);

        Assert.Equal(["home", "mentions"], tab.Columns.Select(c => c.Id));
    }
}
