using NightlyDawn.App.Search;
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
}
