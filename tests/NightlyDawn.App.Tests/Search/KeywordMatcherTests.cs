using NightlyDawn.App.Search;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests.Search;

/// <summary>The fork's keyword rule (StarryEyes <c>SearchResultModel.cs:71-83</c>, plan §37.2 item 10), one clause
/// per test so a mutation names the clause it broke: OR over positive words, no exclusion word present,
/// case-insensitive substring, split on half-width space / tab / full-width space, duplicates dropped.</summary>
public class KeywordMatcherTests
{
    [Fact]
    public void PositiveWords_AreOrEd_NotAndEd()
    {
        // Plan §37.3: the live search in the fork is OR (positive.Any); its pin-to-tab was AND, and this app settles
        // on OR for both. A note containing only one of the two words must match.
        var matcher = KeywordMatcher.Parse("alpha gamma");

        Assert.True(matcher.IsMatch("alpha one"));
        Assert.True(matcher.IsMatch("beta gamma"));
        Assert.False(matcher.IsMatch("beta two"));
    }

    [Fact]
    public void ExclusionWord_RemovesANoteThatWouldOtherwiseMatch()
    {
        var matcher = KeywordMatcher.Parse("alpha -two");

        Assert.True(matcher.IsMatch("alpha one"));
        Assert.False(matcher.IsMatch("alpha two"));
        Assert.Equal(["alpha"], matcher.Positive);
        Assert.Equal(["two"], matcher.Negative);
    }

    [Fact]
    public void Matching_IsCaseInsensitiveSubstring()
    {
        var matcher = KeywordMatcher.Parse("NOSTR");

        Assert.True(matcher.IsMatch("learning nostr today"));
        Assert.True(matcher.IsMatch("Nostr"));
        Assert.False(matcher.IsMatch("n o s t r"));
    }

    [Fact]
    public void Splits_OnHalfWidthSpace_Tab_AndFullWidthSpace_AndDropsDuplicates()
    {
        var matcher = KeywordMatcher.Parse("a\tb　c a  b");

        Assert.Equal(["a", "b", "c"], matcher.Positive);
    }

    [Fact]
    public void OnlyExclusions_MatchNothing_AndReportEmpty()
    {
        // The fork's positive.Any(...) over no positive words is false for every status; IsEmpty lets the caller say
        // "add a word" instead of showing an empty list that reads as "no note contains this".
        var matcher = KeywordMatcher.Parse("-alpha");

        Assert.True(matcher.IsEmpty);
        Assert.False(matcher.IsMatch("beta"));
    }

    [Fact]
    public void BareDash_IsAnEmptyExclusion_ThatExcludesNothing()
    {
        // "" is a substring of everything; without the guard a stray "-" would exclude every note.
        var matcher = KeywordMatcher.Parse("alpha -");

        Assert.True(matcher.IsMatch("alpha"));
    }

    /// <summary>L7b's pin: the <see cref="FilterNode"/> <see cref="KeywordMatcher.ToFilterNode"/> builds must
    /// accept and reject exactly what <see cref="KeywordMatcher.IsMatch"/> does, checked directly against the
    /// node (no KQL text, no compiler) so a mismatch names which side is wrong.</summary>
    [Fact]
    public void ToFilterNode_AcceptsAndRejectsTheSameContentAsIsMatch()
    {
        var matcher = KeywordMatcher.Parse("alpha gamma -two");
        var node = matcher.ToFilterNode();

        Assert.True(Evaluate(node, "alpha one"));
        Assert.True(Evaluate(node, "beta gamma"));
        Assert.False(Evaluate(node, "beta two")); // no positive word.
        Assert.False(Evaluate(node, "alpha two")); // excluded.
        Assert.Equal(matcher.IsMatch("alpha one"), Evaluate(node, "alpha one"));
        Assert.Equal(matcher.IsMatch("alpha two"), Evaluate(node, "alpha two"));
    }

    [Fact]
    public void ToFilterNode_WithOnlyExclusions_Throws()
    {
        // IsEmpty's whole point (OnlyExclusions_MatchNothing_AndReportEmpty above) is that callers check it
        // before building a result to pin; ToFilterNode has no "match nothing" node to hand back instead.
        var matcher = KeywordMatcher.Parse("-alpha");

        Assert.Throws<InvalidOperationException>(() => matcher.ToFilterNode());
    }

    // A tiny evaluator over the three node shapes ToFilterNode can produce (Comparison/Not/And/Or), checked only
    // against a note's content -- independent of KqlCompiler, so this test cannot pass merely because the
    // compiler also gets ToFilterNode's shape right.
    private static bool Evaluate(FilterNode node, string content) => node switch
    {
        FilterComparison(_, FilterComparisonOperator.Contains, var value) => content.Contains(value, StringComparison.OrdinalIgnoreCase),
        FilterNot(var operand) => !Evaluate(operand, content),
        FilterAnd(var left, var right) => Evaluate(left, content) && Evaluate(right, content),
        FilterOr(var left, var right) => Evaluate(left, content) || Evaluate(right, content),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown FilterNode."),
    };
}
