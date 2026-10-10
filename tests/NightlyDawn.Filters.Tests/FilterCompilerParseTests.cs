using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.Filters.Tests;

public class FilterCompilerParseTests
{
    private readonly FilterCompiler _sut = new();

    [Theory]
    [InlineData("from home where ()", FilterSourceKind.Home)]
    [InlineData("from mentions where ()", FilterSourceKind.Mentions)]
    [InlineData("FROM HOME WHERE ()", FilterSourceKind.Home)]
    [InlineData("From Home Where ()", FilterSourceKind.Home)]
    public void Parse_ReadsArgumentlessSources(string kql, FilterSourceKind expectedKind)
    {
        var ast = _sut.Parse(kql);

        Assert.Equal(new FilterAst(new FilterSource(expectedKind)), ast);
    }

    [Theory]
    [InlineData("from user(\"npub1example\") where ()", FilterSourceKind.User, "npub1example")]
    [InlineData("from list(\"naddr1example\") where ()", FilterSourceKind.List, "naddr1example")]
    [InlineData("from search(\"nostr\") where ()", FilterSourceKind.Search, "nostr")]
    [InlineData("from relay(\"wss://relay.example\") where ()", FilterSourceKind.Relay, "wss://relay.example")]
    [InlineData("from kind(\"1\") where ()", FilterSourceKind.Kind, "1")]
    public void Parse_ReadsParameterizedSources_WithRawUnvalidatedArgument(string kql, FilterSourceKind expectedKind, string expectedArgument)
    {
        var ast = _sut.Parse(kql);

        Assert.Equal(new FilterAst(new FilterSource(expectedKind, expectedArgument)), ast);
    }

    [Fact]
    public void Parse_EmptyParenthesesAsTheWholeWhereClause_MeansNoPredicate()
    {
        var ast = _sut.Parse("from home where ()");

        Assert.Null(ast.Where);
    }

    [Theory]
    [InlineData("from home where kind = 1", FilterComparisonOperator.Equals)]
    [InlineData("from home where kind == 1", FilterComparisonOperator.Equals)]
    [InlineData("from home where kind != 1", FilterComparisonOperator.NotEquals)]
    [InlineData("from home where created_at > 1", FilterComparisonOperator.GreaterThan)]
    [InlineData("from home where created_at < 1", FilterComparisonOperator.LessThan)]
    public void Parse_MapsSymbolicOperators(string kql, FilterComparisonOperator expected)
    {
        var ast = _sut.Parse(kql);

        var comparison = Assert.IsType<FilterComparison>(ast.Where);
        Assert.Equal(expected, comparison.Operator);
    }

    [Fact]
    public void Parse_MapsContainsKeyword()
    {
        var ast = _sut.Parse("from home where text contains \"hello\"");

        Assert.Equal(new FilterComparison("text", FilterComparisonOperator.Contains, "hello"), ast.Where);
    }

    [Theory]
    [InlineData("user.npub")]
    [InlineData("user.name")]
    [InlineData("user.nip05")]
    [InlineData("tags.t")]
    public void Parse_AcceptsDotChainFieldNames(string field)
    {
        var ast = _sut.Parse($"from home where {field} = \"x\"");

        var comparison = Assert.IsType<FilterComparison>(ast.Where);
        Assert.Equal(field, comparison.Field);
    }

    [Fact]
    public void Parse_BuildsAndTree()
    {
        var ast = _sut.Parse("from home where kind = 1 & reply = true");

        var and = Assert.IsType<FilterAnd>(ast.Where);
        Assert.Equal(new FilterComparison("kind", FilterComparisonOperator.Equals, "1"), and.Left);
        Assert.Equal(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"), and.Right);
    }

    [Fact]
    public void Parse_BuildsOrTree()
    {
        var ast = _sut.Parse("from home where kind = 1 | kind = 6");

        var or = Assert.IsType<FilterOr>(ast.Where);
        Assert.Equal(new FilterComparison("kind", FilterComparisonOperator.Equals, "1"), or.Left);
        Assert.Equal(new FilterComparison("kind", FilterComparisonOperator.Equals, "6"), or.Right);
    }

    [Fact]
    public void Parse_BuildsNotNode()
    {
        var ast = _sut.Parse("from home where !reply = true");

        var not = Assert.IsType<FilterNot>(ast.Where);
        Assert.Equal(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"), not.Operand);
    }

    [Fact]
    public void Parse_AllowsChainedNot()
    {
        var ast = _sut.Parse("from home where !!reply = true");

        var outer = Assert.IsType<FilterNot>(ast.Where);
        var inner = Assert.IsType<FilterNot>(outer.Operand);
        Assert.Equal(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"), inner.Operand);
    }

    [Fact]
    public void Parse_ParenthesesGroupBeforeAnd()
    {
        var ast = _sut.Parse("from home where (kind = 1 | kind = 6) & reply = true");

        var and = Assert.IsType<FilterAnd>(ast.Where);
        var or = Assert.IsType<FilterOr>(and.Left);
        Assert.Equal(new FilterComparison("kind", FilterComparisonOperator.Equals, "1"), or.Left);
        Assert.Equal(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"), and.Right);
    }

    [Fact]
    public void Parse_RejectsMultipleCommaSeparatedSources_AsRecognizedButUnsupported()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home, mentions where ()"));

        Assert.Contains("recognizes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("comma-separated sources", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingFromClause()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("where ()"));

        Assert.Contains("'from <source>'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsMissingWhereClause()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home"));

        Assert.Contains("'where'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnknownField_AsNotRecognized()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home where bogus = \"x\""));

        Assert.Contains("Unknown KQL field", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsEmptyParentheses_NestedInsideALargerExpression()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home where (kind = 1) & ()"));

        Assert.Contains("only supported as the entire 'where' clause", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsTrailingGarbageAfterTheWhereClause()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home where () extra"));

        Assert.Contains("Unexpected token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsQueriesLongerThanTheLengthLimit()
    {
        var kql = "from home where text contains \"" + new string('a', 9000) + "\"";

        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse(kql));

        Assert.Contains("8192", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsUnclosedQuotedStrings()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home where text contains \"never closed"));

        Assert.Contains("never closed", ex.Message, StringComparison.Ordinal);
    }

    // kql.txt's grammar accepts these; 0d's FilterNode/FilterComparisonOperator cannot represent them.
    // Plan §7.1/§7.5 item ③: the rejection must read as "recognized but unsupported", not "syntax error", and
    // must never silently land on the nearest supported operator (startswith ↛ contains is the explicit example).
    [Theory]
    [InlineData("from home where created_at >= 1", "'>='")]
    [InlineData("from home where created_at <= 1", "'<='")]
    [InlineData("from home where user.npub -> \"x\"", "'->'")]
    [InlineData("from home where user.npub <- \"x\"", "'<-'")]
    [InlineData("from home where user.npub in \"x\"", "'in'")]
    [InlineData("from home where text startswith \"x\"", "'startswith'")]
    [InlineData("from home where text startwith \"x\"", "'startswith'")]
    [InlineData("from home where text endswith \"x\"", "'endswith'")]
    [InlineData("from home where text match \"x\"", "'match'")]
    [InlineData("from home where text regex \"x\"", "'match'")]
    [InlineData("from home where kind + 1 = 2", "arithmetic")]
    [InlineData("from home where kind - 1 = 2", "arithmetic")]
    [InlineData("from home where kind * 1 = 2", "arithmetic")]
    [InlineData("from home where kind / 1 = 2", "arithmetic")]
    public void Parse_RecognizesButRejects_ConstructsTheAstCannotRepresent(string kql, string mustMention)
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse(kql));

        Assert.Contains("recognizes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("but this version does not support it yet", ex.Message, StringComparison.Ordinal);
        Assert.Contains(mustMention, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_StartswithRejection_IsDistinguishableFromAGenuineSyntaxError()
    {
        var recognizedButUnsupported = Assert.Throws<FilterParseException>(
            () => _sut.Parse("from home where text startswith \"x\""));
        var genuineSyntaxError = Assert.Throws<FilterParseException>(
            () => _sut.Parse("from home where text \"x\""));

        Assert.DoesNotContain("Unexpected token", recognizedButUnsupported.Message, StringComparison.Ordinal);
        Assert.Contains("Unexpected token", genuineSyntaxError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAtLiteralImmediateUserValue()
    {
        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse("from home where user.npub = @alice"));

        Assert.Contains("@literal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsNestingDeeperThanTheMaximum()
    {
        var opens = string.Concat(Enumerable.Repeat("(", 40));
        var closes = string.Concat(Enumerable.Repeat(")", 40));
        var kql = $"from home where {opens}kind = 1{closes}";

        var ex = Assert.Throws<FilterParseException>(() => _sut.Parse(kql));

        Assert.Contains("nests more than 32 levels", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AllowsNestingUpToTheMaximum()
    {
        var opens = string.Concat(Enumerable.Repeat("(", 32));
        var closes = string.Concat(Enumerable.Repeat(")", 32));
        var kql = $"from home where {opens}kind = 1{closes}";

        var ast = _sut.Parse(kql);

        Assert.Equal(new FilterComparison("kind", FilterComparisonOperator.Equals, "1"), ast.Where);
    }
}
