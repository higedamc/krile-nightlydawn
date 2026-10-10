using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.Filters.Tests;

public class FilterCompilerCompileTests
{
    private readonly FilterCompiler _sut = new();

    [Theory]
    [InlineData(FilterSourceKind.Home, "from home")]
    [InlineData(FilterSourceKind.Mentions, "from mentions")]
    [InlineData(FilterSourceKind.List, "from list")]
    public void Compile_RejectsSourcesThatNeedAnAccountItDoesNotHave(FilterSourceKind kind, string expectedField)
    {
        var ast = new FilterAst(new FilterSource(kind, kind == FilterSourceKind.List ? "naddr1example" : null));

        var ex = Assert.Throws<UnsupportedFilterFieldException>(() => _sut.Compile(ast));

        Assert.Equal(expectedField, ex.Field);
    }

    [Fact]
    public void Compile_UserSource_ResolvesNpubToHexAuthorsFilter()
    {
        var npub = Bech32.Encode("npub", Convert.FromHexString(TestNotes.AuthorHex));
        var ast = new FilterAst(new FilterSource(FilterSourceKind.User, npub));

        var compiled = _sut.Compile(ast);

        Assert.Equal([TestNotes.AuthorHex], compiled.RelayFilter.Authors);
        Assert.Null(compiled.LocalPredicate);
    }

    [Fact]
    public void Compile_UserSource_AcceptsRawHex()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.User, TestNotes.AuthorHex));

        var compiled = _sut.Compile(ast);

        Assert.Equal([TestNotes.AuthorHex], compiled.RelayFilter.Authors);
    }

    [Fact]
    public void Compile_UserSource_RejectsAMalformedArgument()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.User, "not-a-pubkey"));

        Assert.Throws<FilterParseException>(() => _sut.Compile(ast));
    }

    [Fact]
    public void Compile_KindSource_SetsRelayFilterKinds()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Kind, "1"));

        var compiled = _sut.Compile(ast);

        Assert.Equal([1], compiled.RelayFilter.Kinds);
    }

    [Fact]
    public void Compile_KindSource_RejectsOutOfRangeArgument()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Kind, "99999999"));

        Assert.Throws<FilterParseException>(() => _sut.Compile(ast));
    }

    [Fact]
    public void Compile_SearchSource_NeverEmitsNostrFilterSearch_AndFiltersLocallyInstead()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Search, "nostr"));

        var compiled = _sut.Compile(ast);

        Assert.Null(compiled.RelayFilter.Search);
        Assert.Null(compiled.RelayFilter.Kinds);
        Assert.Null(compiled.RelayFilter.Authors);
        Assert.NotNull(compiled.LocalPredicate);
        Assert.True(compiled.LocalPredicate(TestNotes.Make(content: "a nostr note")));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(content: "no match here")));
    }

    [Fact]
    public void Compile_RelaySource_FiltersLocallyByFirstSeenOnRelay()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Relay, "wss://relay.example"));

        var compiled = _sut.Compile(ast);

        Assert.Null(compiled.RelayFilter.Kinds);
        Assert.Null(compiled.RelayFilter.Authors);
        Assert.NotNull(compiled.LocalPredicate);
        Assert.True(compiled.LocalPredicate(TestNotes.Make(firstSeenOnRelay: RelayUrl.Parse("wss://relay.example"))));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(firstSeenOnRelay: RelayUrl.Parse("wss://other.example"))));
    }

    [Fact]
    public void Compile_RelaySource_RejectsAMalformedUrl()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Relay, "not a url"));

        Assert.Throws<FilterParseException>(() => _sut.Compile(ast));
    }

    [Fact]
    public void Compile_HasNoLocalPredicate_WhenNeitherSourceNorWhereNeedOne()
    {
        var ast = new FilterAst(new FilterSource(FilterSourceKind.Kind, "1"));

        var compiled = _sut.Compile(ast);

        Assert.Null(compiled.LocalPredicate);
    }

    // --- Soundness (plan §7.2/§7.5 item ①): RelayFilter must stay a superset of what the AST accepts. ---

    [Fact]
    public void Compile_PushesDownTopLevelAndConditions_IntoRelayFilter()
    {
        var npub = TestNotes.AuthorHex;
        var ast = new FilterCompiler().Parse(
            $"from search(\"\") where kind = 1 & user.npub = \"{npub}\" & tags.t = \"nostr\" & created_at > 100");

        var compiled = _sut.Compile(ast);

        Assert.Equal([1], compiled.RelayFilter.Kinds);
        Assert.Equal([npub], compiled.RelayFilter.Authors);
        Assert.Equal(["nostr"], compiled.RelayFilter.TagFilters?["t"]);
        Assert.Equal(100, compiled.RelayFilter.Since);
    }

    [Fact]
    public void Compile_DoesNotPushDownThroughOr_SoRelayFilterStaysASuperset()
    {
        var ast = _sut.Parse("from search(\"\") where kind = 1 | kind = 6");

        var compiled = _sut.Compile(ast);

        // The real assertion: an implementation that (wrongly) pushed the Or's left branch down would set
        // Kinds=[1], and a relay given that filter would never deliver the kind:6 note this AST still accepts.
        Assert.Null(compiled.RelayFilter.Kinds);
        Assert.True(compiled.LocalPredicate!(TestNotes.Make(kind: NoteKind.Repost)));
        Assert.True(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.Text)));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.GenericRepost)));
    }

    [Fact]
    public void Compile_DoesNotPushDownThroughNot_SoRelayFilterStaysASuperset()
    {
        var ast = _sut.Parse("from search(\"\") where !(kind = 1)");

        var compiled = _sut.Compile(ast);

        Assert.Null(compiled.RelayFilter.Kinds);
        Assert.False(compiled.LocalPredicate!(TestNotes.Make(kind: NoteKind.Text)));
        Assert.True(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.Repost)));
    }

    [Fact]
    public void Compile_LocalPredicateAlwaysReevaluatesTheWholeWhereClause_EvenWhenConditionsWerePushedDown()
    {
        // A note that satisfies the pushed-down Kinds=[1] filter in isolation but fails the full Where
        // (reply must also be true) must still be rejected locally — RelayFilter being a superset does not
        // mean LocalPredicate can skip re-checking what it pushed down.
        var ast = _sut.Parse("from search(\"\") where kind = 1 & reply = true");

        var compiled = _sut.Compile(ast);

        Assert.True(compiled.LocalPredicate!(TestNotes.Make(kind: NoteKind.Text, replyId: "parent")));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.Text, replyId: null)));
    }

    // --- Unsupported fields (plan §7.3/§7.5 item ②): must be rejected everywhere in the tree, not just at the top. ---

    [Theory]
    [InlineData("user.name")]
    [InlineData("user.nip05")]
    [InlineData("reactions")]
    [InlineData("reposts")]
    [InlineData("relay")]
    public void Compile_RejectsUnsupportedFields_AtTopLevel(string field)
    {
        var ast = _sut.Parse($"from search(\"\") where {field} = \"x\"");

        var ex = Assert.Throws<UnsupportedFilterFieldException>(() => _sut.Compile(ast));
        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void Compile_RejectsUnsupportedFields_UnderNot()
    {
        var ast = _sut.Parse("from search(\"\") where !(reactions > \"5\")");

        var ex = Assert.Throws<UnsupportedFilterFieldException>(() => _sut.Compile(ast));
        Assert.Equal("reactions", ex.Field);
    }

    [Fact]
    public void Compile_RejectsUnsupportedFields_UnderOr()
    {
        // The real assertion: "this branch of the Or is never pushed down, so it's safe to ignore" is wrong —
        // LocalPredicate evaluates every branch of the Where tree, including this one, so an unevaluable field
        // here is just as fatal as at the top level.
        var ast = _sut.Parse("from search(\"\") where text contains \"a\" | reposts = \"0\"");

        var ex = Assert.Throws<UnsupportedFilterFieldException>(() => _sut.Compile(ast));
        Assert.Equal("reposts", ex.Field);
    }

    // --- Field evaluation ---

    [Fact]
    public void Evaluate_Reply_IsTrueOnlyWhenReplyIdIsSet()
    {
        var ast = _sut.Parse("from search(\"\") where reply = true");
        var compiled = _sut.Compile(ast);

        Assert.True(compiled.LocalPredicate!(TestNotes.Make(replyId: "parent")));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(replyId: null)));
    }

    [Fact]
    public void Evaluate_Root_IsTrueOnlyWhenRootIdIsSet()
    {
        var ast = _sut.Parse("from search(\"\") where root = true");
        var compiled = _sut.Compile(ast);

        Assert.True(compiled.LocalPredicate!(TestNotes.Make(rootId: "root")));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(rootId: null)));
    }

    [Fact]
    public void Evaluate_BooleanField_RejectsANonBooleanValue()
    {
        var ast = _sut.Parse("from search(\"\") where reply = \"yes\"");
        var compiled = _sut.Compile(ast);

        Assert.Throws<FilterParseException>(() => compiled.LocalPredicate!(TestNotes.Make(replyId: "parent")));
    }

    [Theory]
    [InlineData(FilterComparisonOperator.Equals, "nostr", true)]
    [InlineData(FilterComparisonOperator.Equals, "other", false)]
    [InlineData(FilterComparisonOperator.Contains, "nos", true)]
    [InlineData(FilterComparisonOperator.NotEquals, "other", true)]
    public void Evaluate_TagsT_MatchesAgainstHashtags(FilterComparisonOperator op, string value, bool expected)
    {
        var opText = op switch
        {
            FilterComparisonOperator.Equals => "=",
            FilterComparisonOperator.Contains => "contains",
            FilterComparisonOperator.NotEquals => "!=",
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        var ast = _sut.Parse($"from search(\"\") where tags.t {opText} \"{value}\"");
        var compiled = _sut.Compile(ast);

        Assert.Equal(expected, compiled.LocalPredicate!(TestNotes.Make(hashtags: ["nostr"])));
    }

    [Fact]
    public void Evaluate_NumericField_RejectsContains()
    {
        var ast = _sut.Parse("from search(\"\") where kind contains \"1\"");
        var compiled = _sut.Compile(ast);

        Assert.Throws<FilterParseException>(() => compiled.LocalPredicate!(TestNotes.Make()));
    }
}
