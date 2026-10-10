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
        // A User source always carries its own LocalPredicate now (plan §7.6 item ②), so a later `where` can
        // never union Authors wider than the source without the source re-asserting itself locally.
        Assert.NotNull(compiled.LocalPredicate);
        Assert.True(compiled.LocalPredicate!(TestNotes.Make(authorPubkey: TestNotes.AuthorHex)));
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
        // Push-down leaves Kinds empty (neither the source nor the Where constrains it), so Compile defaults
        // it to every kind a Note can represent (plan §7.6 item ④) rather than leaving it null/unconstrained.
        Assert.Equal([1, 6, 16], compiled.RelayFilter.Kinds);
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

        Assert.Equal([1, 6, 16], compiled.RelayFilter.Kinds);
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

    // --- Source predicates (plan §7.6 item ②): a source is a hard restriction, not just a relay-filter hint. ---

    [Fact]
    public void Compile_UserSource_LocalPredicateStaysRestrictedToTheSourceAuthor_EvenWhenWherePushesDownADifferentAuthor()
    {
        // The real assertion: `where user.npub = B` unions RelayFilter.Authors to [A, B] (plan §7.2's superset
        // rule), so a column asking `from user(A)` must not show B's notes just because B's own notes satisfy
        // the Where clause in isolation — the source's own author has to be re-checked locally too.
        var a = TestNotes.AuthorHex;
        const string b = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var ast = _sut.Parse($"from user(\"{a}\") where user.npub = \"{b}\"");

        var compiled = _sut.Compile(ast);

        Assert.Equal([a, b], compiled.RelayFilter.Authors);
        Assert.False(compiled.LocalPredicate!(TestNotes.Make(authorPubkey: b)));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(authorPubkey: a)));
    }

    [Fact]
    public void Compile_KindSource_LocalPredicateStaysRestrictedToTheSourceKind_EvenWhenWherePushesDownADifferentKind()
    {
        var ast = _sut.Parse("from kind(\"1\") where kind = 6");

        var compiled = _sut.Compile(ast);

        Assert.Equal([1, 6], compiled.RelayFilter.Kinds);
        Assert.False(compiled.LocalPredicate!(TestNotes.Make(kind: NoteKind.Repost)));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.Text)));
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
        // Push-down leaves Kinds empty here (Or is never pushed down), so Compile's plan §7.6 item ④ default
        // takes over — note that default is every kind a Note can represent, not just [1], so it stays a
        // superset of what this Or actually accepts.
        Assert.Equal([1, 6, 16], compiled.RelayFilter.Kinds);
        Assert.True(compiled.LocalPredicate!(TestNotes.Make(kind: NoteKind.Repost)));
        Assert.True(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.Text)));
        Assert.False(compiled.LocalPredicate(TestNotes.Make(kind: NoteKind.GenericRepost)));
    }

    [Fact]
    public void Compile_DoesNotPushDownThroughNot_SoRelayFilterStaysASuperset()
    {
        var ast = _sut.Parse("from search(\"\") where !(kind = 1)");

        var compiled = _sut.Compile(ast);

        Assert.Equal([1, 6, 16], compiled.RelayFilter.Kinds);
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
        // plan §7.6 item ①: this must fail at Compile, not on the first note handed to LocalPredicate —
        // a bad query is a column that never loads, not one that throws partway through a live timeline.
        var ast = _sut.Parse("from search(\"\") where reply = \"yes\"");

        Assert.Throws<FilterParseException>(() => _sut.Compile(ast));
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
    public void Compile_TagsT_NormalizesTheQueryValueTheSameWayOnBothSides_RegardlessOfItsCasing()
    {
        // plan §7.6 item ③: PushDown and the local predicate must call the same normalization helper, so a
        // mixed-case query value (as a user would type) still matches Note.Hashtags -- which is always
        // lowercased by EventMapper (see Entities.cs's Note doc comment) -- on both the relay-filter side and
        // the local side, rather than drifting if one side's normalization ever changes without the other's.
        var ast = _sut.Parse("from search(\"\") where tags.t = \"Bitcoin\"");

        var compiled = _sut.Compile(ast);

        Assert.Equal(["bitcoin"], compiled.RelayFilter.TagFilters?["t"]);
        Assert.True(compiled.LocalPredicate!(TestNotes.Make(hashtags: ["bitcoin"])));
    }

    [Fact]
    public void Evaluate_NumericField_RejectsContains()
    {
        // plan §7.6 item ①: same as the boolean case above — fails at Compile, not on the first note.
        var ast = _sut.Parse("from search(\"\") where kind contains \"1\"");

        Assert.Throws<FilterParseException>(() => _sut.Compile(ast));
    }

    // --- Compile-time value validation (plan §7.6 item ①): every value Compile does not push down still has to
    // type-check against its field, and must do so at Compile — not lazily, the first time a note reaches
    // LocalPredicate partway through an already-loaded timeline. ---

    [Theory]
    [InlineData("from search(\"\") where created_at < \"abc\"")]
    [InlineData("from search(\"\") where user.npub != \"zzz\"")]
    [InlineData("from search(\"\") where reply = \"maybe\"")]
    [InlineData("from search(\"\") where kind contains \"1\"")]
    public void Compile_RejectsAMalformedComparisonValue_AtCompileNotAtFirstEvaluation(string kql)
    {
        var ast = _sut.Parse(kql);

        Assert.Throws<FilterParseException>(() => _sut.Compile(ast));
    }
}
