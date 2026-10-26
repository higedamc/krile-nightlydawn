using NightlyDawn.App.Search;
using NightlyDawn.Core;
using NightlyDawn.Filters;
using Xunit;

namespace NightlyDawn.App.Tests.Search;

/// <summary>
/// <see cref="KqlWriter"/> only has one job: <c>Write(Parse(x))</c> must compile to the same decisions as
/// <c>x</c> itself (plan §41 item ②'s "pin 前と pin 後で出ている集合が一致する" needs exactly this, one layer
/// down from <see cref="SearchFlipViewModel.PinToTab"/>). Every test here parses a KQL string, writes it back,
/// re-parses the written text, compiles both ASTs and checks the two <see cref="CompiledFilter"/>s make the
/// same call on a set of fixture notes -- not that the written text looks a particular way, which the compiler
/// does not care about.
/// </summary>
public class KqlWriterTests
{
    private static readonly IFilterCompiler Compiler = new FilterCompiler();

    private static readonly Note[] Notes =
    [
        new(Id: "1".PadLeft(64, '0'), AuthorPubkey: "a".PadLeft(64, '0'), CreatedAt: 100, Kind: NoteKind.Text, Content: "alpha one", Tags: []),
        new(Id: "2".PadLeft(64, '0'), AuthorPubkey: "b".PadLeft(64, '0'), CreatedAt: 200, Kind: NoteKind.Repost, Content: "beta two", Tags: []),
        new(Id: "3".PadLeft(64, '0'), AuthorPubkey: "a".PadLeft(64, '0'), CreatedAt: 300, Kind: NoteKind.Text, Content: "alpha \"gamma\" \\slash", Tags: []),
    ];

    [Theory]
    [InlineData("from kind(\"1\") where (text contains \"alpha\")")]
    [InlineData("from kind(\"1\") where (text contains \"alpha\" && !(text contains \"gamma\"))")]
    [InlineData("from kind(\"1\") where (text contains \"alpha\" || text contains \"beta\")")]
    [InlineData("from kind(\"1\") where (!(text contains \"alpha\"))")]
    [InlineData("from kind(\"1\") where ()")]
    [InlineData("from user(\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\") where (kind = 1)")]
    [InlineData("from search(\"nostr\") where ()")]
    [InlineData("from relay(\"wss://relay.example\") where ()")]
    [InlineData("from kind(\"1\") where (created_at > 150 && created_at < 250)")]
    public void WriteOfParse_CompilesToTheSameDecisionsAsTheOriginal(string kql)
    {
        var originalAst = Compiler.Parse(kql);
        var rewritten = KqlWriter.Write(originalAst);
        var rewrittenAst = Compiler.Parse(rewritten); // Must still be valid KQL; throws FilterParseException otherwise.

        var originalCompiled = Compiler.Compile(originalAst);
        var rewrittenCompiled = Compiler.Compile(rewrittenAst);

        // NostrFilter's list-typed members compare by reference under the record-generated Equals (List<T> has
        // no value equality of its own), so a structural check (Equivalent) is what "the same filter" means here.
        Assert.Equivalent(originalCompiled.RelayFilter, rewrittenCompiled.RelayFilter, strict: true);
        foreach (var note in Notes)
        {
            Assert.Equal(Accepts(originalCompiled, note), Accepts(rewrittenCompiled, note));
        }
    }

    /// <summary>Plan §41 item ②, the shape <see cref="SearchFlipViewModel.PinToTab"/> actually builds: the
    /// current tab's own <c>where</c> ANDed with the keyword expression. Checked by hand here (not through
    /// <see cref="SearchFlipViewModel"/>) so a failure names <see cref="KqlWriter"/>, not the view model around
    /// it.</summary>
    [Fact]
    public void AndingAKeywordNodeOntoAnExistingWhere_MatchesEvaluatingBothSeparately()
    {
        var tabAst = Compiler.Parse("from kind(\"1\") where (kind = 1)");
        var keywordNode = KeywordMatcher.Parse("alpha -beta").ToFilterNode();
        var combinedAst = new FilterAst(tabAst.Source, tabAst.Where is null ? keywordNode : new FilterAnd(tabAst.Where, keywordNode));

        var rewritten = KqlWriter.Write(combinedAst);
        var rewrittenCompiled = Compiler.Compile(Compiler.Parse(rewritten));

        foreach (var note in Notes)
        {
            var expected = note.Kind == NoteKind.Text && KeywordMatcher.Parse("alpha -beta").IsMatch(note.Content);
            Assert.Equal(expected, Accepts(rewrittenCompiled, note));
        }
    }

    /// <summary>A value containing both a quote and a backslash, built in C# (not hand-escaped in a KQL string
    /// literal, which is itself error-prone to get right): <see cref="KqlWriter"/>'s own <c>Quote</c> must escape
    /// it so re-parsing the written text yields the exact same <see cref="FilterComparison.Value"/> back.</summary>
    [Fact]
    public void WritingAValueWithQuotesAndBackslashes_RoundTripsExactly()
    {
        var value = "has \"quotes\" and a \\ backslash";
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Kind, "1"), new FilterComparison("text", FilterComparisonOperator.Contains, value));

        var rewritten = KqlWriter.Write(ast);
        var rewrittenAst = Compiler.Parse(rewritten);

        var comparison = Assert.IsType<FilterComparison>(rewrittenAst.Where);
        Assert.Equal(value, comparison.Value);
    }

    private static bool Accepts(CompiledFilter compiled, Note note) =>
        (compiled.RelayFilter.Kinds?.Contains((int)note.Kind) ?? true) && (compiled.LocalPredicate?.Invoke(note) ?? true);
}
