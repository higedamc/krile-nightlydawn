using System.Linq;

namespace NightlyDawn.App.Search;

/// <summary>
/// The fork's keyword rule, ported verbatim from StarryEyes <c>Models/Timelines/SearchFlips/SearchResultModel.cs:71-83</c>
/// (plan §37.2 item 10): the text is split on half-width space, tab and full-width space (U+3000), duplicates are
/// dropped, a word starting with <c>-</c> is an exclusion, and a note matches when <b>any</b> positive word occurs
/// in it (OR) and <b>no</b> exclusion word does, comparing case-insensitively as a substring.
///
/// <para>Two deliberate differences from the fork, both recorded in the readme's Search section:
/// (1) comparison is <see cref="StringComparison.OrdinalIgnoreCase"/> rather than the fork's
/// <c>CurrentCultureIgnoreCase</c>, because .NET on ICU treats some characters as ignorable in culture-aware
/// <c>IndexOf</c> (an empty or zero-width needle matches everywhere), and note content is relay-supplied text;
/// ordinal makes the rule the same on macOS, Linux and in CI. (2) The fork's <c>PinToTab</c> generated an AND over
/// the same positive words (<c>SearchResultModel.cs:160</c>) -- a live result that changed shape when pinned.
/// Plan §37.3 settles that on OR for both; L7b's pin reuses <see cref="Parse"/> so there is one rule, not two.</para>
///
/// <para>Pure and allocation-light on purpose: <see cref="SearchFlipViewModel"/> re-runs it on every keystroke in
/// <see cref="SearchMode.CurrentTab"/>, over up to <see cref="Timelines.TimelineViewModel.DefaultMaxNotes"/> notes.</para>
/// </summary>
public sealed class KeywordMatcher
{
    private static readonly string[] Separators = [" ", "\t", "　"];

    private readonly string[] _positive;
    private readonly string[] _negative;

    private KeywordMatcher(string[] positive, string[] negative)
    {
        _positive = positive;
        _negative = negative;
    }

    /// <summary>Words that must occur (any one of them) -- every token not starting with <c>-</c>, deduplicated, in
    /// input order.</summary>
    public IReadOnlyList<string> Positive => _positive;

    /// <summary>Words that must not occur -- every token starting with <c>-</c>, with that <c>-</c> removed. A bare
    /// <c>-</c> token becomes an empty exclusion, which the fork also produced; <see cref="IsMatch"/> ignores empty
    /// exclusions rather than letting "" match every note and exclude everything.</summary>
    public IReadOnlyList<string> Negative => _negative;

    /// <summary>True when there is nothing to match on: no positive word at all. The fork's <c>positive.Any(...)</c>
    /// over an empty array is false for every note, so a query of only exclusions matches nothing; callers can use
    /// this to say so instead of showing an empty list.</summary>
    public bool IsEmpty => _positive.Length == 0;

    public static KeywordMatcher Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var positive = tokens.Where(t => !t.StartsWith('-')).ToArray();
        var negative = tokens.Where(t => t.StartsWith('-')).Select(t => t[1..]).ToArray();
        return new KeywordMatcher(positive, negative);
    }

    /// <summary>The fork's predicate, line for line: <c>positive.Any(contains) &amp;&amp; !negative.Any(contains)</c>.</summary>
    public bool IsMatch(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var anyPositive = false;
        foreach (var word in _positive)
        {
            if (content.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                anyPositive = true;
                break;
            }
        }

        if (!anyPositive)
        {
            return false;
        }

        foreach (var word in _negative)
        {
            if (word.Length > 0 && content.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
