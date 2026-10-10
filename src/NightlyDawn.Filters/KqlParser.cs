using NightlyDawn.Core;

namespace NightlyDawn.Filters;

/// <summary>
/// Turns KQL text into a <see cref="FilterAst"/>. Grammar is kql.txt; precedence-climbing structure (OR binds
/// loosest, then AND, then NOT, then comparisons) is ported from StarryEyes/Filters/Parsing/QueryCompiler.cs's
/// CompileL0..L8, collapsed to the levels plan §7.1 keeps (0d's <see cref="FilterNode"/> has no arithmetic,
/// set-membership, or regex node, so those StarryEyes precedence levels have nothing to build and are dropped
/// rather than ported dead).
///
/// Two kinds of rejection, both from here, and plan §7.1 requires their messages stay distinguishable:
/// <list type="bullet">
/// <item><see cref="FilterParseException"/> for genuine syntax errors (<see cref="TokenReader.Expect"/>'s
/// messages) — the input does not match kql.txt at all.</item>
/// <item><see cref="FilterParseException"/> for constructs kql.txt's grammar *does* accept but 0d's AST cannot
/// represent (arithmetic, <c>in</c>/<c>contains</c>'s set form, <c>startswith</c>/<c>endswith</c>/<c>match</c>,
/// <c>&lt;=</c>/<c>&gt;=</c>, <c>@literal</c> immediates, multiple comma-separated sources, dot-chains that are
/// not one of the 12 known field names) — <see cref="Unsupported"/> below always says "recognized ... but not
/// supported", never "syntax error", because the input *is* valid KQL (it is 0d's contract that is narrower).
/// </list>
/// Neither case is <see cref="UnsupportedFilterFieldException"/> — that type is reserved for ASTs that parsed
/// cleanly but <see cref="KqlCompiler"/> cannot compile because the field/source needs data Compile doesn't have
/// (plan §1 item B, §7.3).
/// </summary>
internal static class KqlParser
{
    /// <summary>kql.txt §4's 12 fixed field names (plan §4 / KRILE_NIGHTLYDAWN_PLAN.md:58). A dot-chain that
    /// resolves to anything else is syntactically valid (OBJECT := LITERAL {'.' OBJECT}) but not one of these,
    /// so it is rejected here, not silently accepted as some new field.</summary>
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "text", "kind", "created_at", "user.npub", "user.name", "user.nip05",
        "tags.t", "reply", "root", "reactions", "reposts", "relay",
    };

    private static readonly Dictionary<string, FilterSourceKind> SourceKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = FilterSourceKind.Home,
        ["mentions"] = FilterSourceKind.Mentions,
        ["user"] = FilterSourceKind.User,
        ["list"] = FilterSourceKind.List,
        ["search"] = FilterSourceKind.Search,
        ["relay"] = FilterSourceKind.Relay,
        ["kind"] = FilterSourceKind.Kind,
    };

    private const int MaxNestingDepth = 32;

    /// <exception cref="FilterParseException">The input is not valid KQL.</exception>
    public static FilterAst Parse(string kql)
    {
        ArgumentNullException.ThrowIfNull(kql);

        var tokens = Tokenizer.Tokenize(kql);
        var reader = new TokenReader(tokens);

        var source = ParseFromClause(reader);
        var where = ParseWhereClause(reader);

        if (reader.HasNext)
        {
            throw new FilterParseException($"Unexpected token {reader.Peek()} after the end of the KQL query.");
        }

        return new FilterAst(source, where);
    }

    // SOURCES := SOURCE {',' SOURCES}; the KQL `from` clause is optional in kql.txt, but FilterAst.Source is a
    // single, non-optional value, so "from" absent has no AST to produce and is rejected here rather than
    // guessing a default source (that would be a product decision, not a parsing one).
    private static FilterSource ParseFromClause(TokenReader reader)
    {
        if (!reader.HasNext || !IsLiteralKeyword(reader.Peek(), "from"))
        {
            throw new FilterParseException("KQL query must start with a 'from <source>' clause.");
        }

        reader.Next();

        var sources = new List<FilterSource> { ParseSource(reader) };
        while (reader.HasNext && reader.Peek().Type == TokenType.Comma)
        {
            reader.Next();
            sources.Add(ParseSource(reader));
        }

        if (sources.Count > 1)
        {
            throw Unsupported($"{sources.Count} comma-separated sources (FilterAst holds a single FilterSource)");
        }

        return sources[0];
    }

    // SOURCE := LITERAL | LITERAL '(' STRING ')'. Unlike StarryEyes' `source: "arg"` colon syntax, kql.txt and
    // plan §4's own examples (`user(npub1…)`, `kind(1)`) use parentheses, so that is what this parses.
    private static FilterSource ParseSource(TokenReader reader)
    {
        var name = reader.Expect(TokenType.Literal).Value!;
        if (!SourceKeywords.TryGetValue(name, out var kind))
        {
            throw new FilterParseException($"Unknown KQL source '{name}'. Expected one of: {string.Join(", ", SourceKeywords.Keys)}.");
        }

        if (kind is FilterSourceKind.Home or FilterSourceKind.Mentions)
        {
            return new FilterSource(kind);
        }

        reader.Expect(TokenType.OpenParenthesis);
        var argument = reader.Expect(TokenType.String).Value!;
        reader.Expect(TokenType.CloseParenthesis);
        return new FilterSource(kind, argument);
    }

    // `where` is mandatory in kql.txt (only the `from` clause is wrapped in {}). `where ()` is this grammar's
    // only way to spell "no predicate" (0d's FilterNode has no always-true leaf to parse a bare empty-bracket
    // into anywhere else), so it is special-cased here, at the top, rather than inside the expression grammar.
    private static FilterNode? ParseWhereClause(TokenReader reader)
    {
        if (!reader.HasNext || !IsLiteralKeyword(reader.Peek(), "where"))
        {
            throw new FilterParseException("KQL query must contain a 'where' clause (write 'where ()' to match everything).");
        }

        reader.Next();

        if (reader.HasNext && reader.Peek().Type == TokenType.OpenParenthesis)
        {
            var afterOpen = PeekSecond(reader);
            if (afterOpen is { Type: TokenType.CloseParenthesis })
            {
                reader.Next();
                reader.Next();
                return null;
            }
        }

        return ParseOr(reader, depth: 0);
    }

    private static Token? PeekSecond(TokenReader reader)
    {
        // Only used right after confirming an OpenParenthesis is next, so a tiny private lookahead copy is
        // simpler than threading a general 2-token-lookahead API through TokenReader for this one call site.
        reader.Next();
        Token? second = reader.HasNext ? reader.Peek() : null;
        reader.PushBack();
        return second;
    }

    // L0 (loosest): OR. Left-associative; FilterOr's Left/Right do not care about associativity direction since
    // both sides are evaluated and OR'd regardless, so a simple left-fold (not StarryEyes' right-recursive
    // GenerateSink) keeps AND/OR chains iterative instead of recursive — only '(' and '!' consume depth budget.
    private static FilterNode ParseOr(TokenReader reader, int depth)
    {
        var left = ParseAnd(reader, depth);
        while (reader.HasNext && reader.Peek().Type == TokenType.OperatorOr)
        {
            reader.Next();
            var right = ParseAnd(reader, depth);
            left = new FilterOr(left, right);
        }

        return left;
    }

    // L1: AND.
    private static FilterNode ParseAnd(TokenReader reader, int depth)
    {
        var left = ParseNot(reader, depth);
        while (reader.HasNext && reader.Peek().Type == TokenType.OperatorAnd)
        {
            reader.Next();
            var right = ParseNot(reader, depth);
            left = new FilterAnd(left, right);
        }

        return left;
    }

    // L2: prefix NOT. Recursive (so `!!x` parses), which is why it shares the same depth budget as '('.
    private static FilterNode ParseNot(TokenReader reader, int depth)
    {
        if (reader.HasNext && reader.Peek().Type == TokenType.Exclamation)
        {
            reader.Next();
            var nextDepth = depth + 1;
            if (nextDepth > MaxNestingDepth)
            {
                throw new FilterParseException($"KQL query nests more than {MaxNestingDepth} levels deep.");
            }

            return new FilterNot(ParseNot(reader, nextDepth));
        }

        return ParseAtom(reader, depth);
    }

    // L3 (tightest): '(' EXPRESSIONS ')' or a single comparison.
    private static FilterNode ParseAtom(TokenReader reader, int depth)
    {
        if (reader.HasNext && reader.Peek().Type == TokenType.OpenParenthesis)
        {
            reader.Next();
            if (reader.HasNext && reader.Peek().Type == TokenType.CloseParenthesis)
            {
                throw new FilterParseException("Empty parentheses '()' are only supported as the entire 'where' clause, not nested inside a larger expression.");
            }

            var nextDepth = depth + 1;
            if (nextDepth > MaxNestingDepth)
            {
                throw new FilterParseException($"KQL query nests more than {MaxNestingDepth} levels deep.");
            }

            var inner = ParseOr(reader, nextDepth);
            reader.Expect(TokenType.CloseParenthesis);
            return inner;
        }

        return ParseComparison(reader);
    }

    private static FilterNode ParseComparison(TokenReader reader)
    {
        var field = ParseDotChain(reader);
        if (!KnownFields.Contains(field))
        {
            throw new FilterParseException($"Unknown KQL field '{field}'. This is a recognized dot-chain but not one of the fields this version knows.");
        }

        var op = ParseOperator(reader);
        var value = ParseValue(reader);
        return new FilterComparison(field, op, value);
    }

    // OBJECT := LITERAL {'.' OBJECT}. Shared by the field (LHS) and bare-literal values (RHS) — kql.txt defines
    // both with the same production, and a number like `1700000000` also tokenizes as a bare Literal here.
    private static string ParseDotChain(TokenReader reader)
    {
        var first = reader.Expect(TokenType.Literal).Value!;
        if (!reader.HasNext || reader.Peek().Type != TokenType.Period)
        {
            return first;
        }

        var sb = new System.Text.StringBuilder(first);
        while (reader.HasNext && reader.Peek().Type == TokenType.Period)
        {
            reader.Next();
            sb.Append('.').Append(reader.Expect(TokenType.Literal).Value);
        }

        return sb.ToString();
    }

    private static FilterComparisonOperator ParseOperator(TokenReader reader)
    {
        if (!reader.HasNext)
        {
            throw new FilterParseException("KQL query ends where a comparison operator was expected.");
        }

        var token = reader.Peek();
        switch (token.Type)
        {
            case TokenType.OperatorEquals:
                reader.Next();
                return FilterComparisonOperator.Equals;
            case TokenType.OperatorNotEquals:
                reader.Next();
                return FilterComparisonOperator.NotEquals;
            case TokenType.OperatorGreaterThan:
                reader.Next();
                return FilterComparisonOperator.GreaterThan;
            case TokenType.OperatorLessThan:
                reader.Next();
                return FilterComparisonOperator.LessThan;
            case TokenType.OperatorGreaterThanOrEqual:
                reader.Next();
                throw Unsupported("'>=' (only strict '>' is supported)");
            case TokenType.OperatorLessThanOrEqual:
                reader.Next();
                throw Unsupported("'<=' (only strict '<' is supported)");
            case TokenType.OperatorContains:
                reader.Next();
                throw Unsupported("'->' (set-contains; use the 'contains' keyword for text substring matching)");
            case TokenType.OperatorIn:
                reader.Next();
                throw Unsupported("'<-' (set membership)");
            case TokenType.OperatorPlus:
            case TokenType.OperatorMinus:
            case TokenType.OperatorMultiply:
            case TokenType.OperatorDivide:
                reader.Next();
                throw Unsupported($"arithmetic operator {token} (FilterComparisonOperator has no arithmetic case)");
            case TokenType.Literal:
                return ParseKeywordOperator(reader);
            default:
                throw new FilterParseException($"Unexpected token {token} in KQL query; expected a comparison operator.");
        }
    }

    private static FilterComparisonOperator ParseKeywordOperator(TokenReader reader)
    {
        var token = reader.Next();
        var keyword = token.Value!;
        switch (keyword.ToLowerInvariant())
        {
            case "contains":
                return FilterComparisonOperator.Contains;
            case "in":
                throw Unsupported("'in' (set membership)");
            case "startswith" or "startwith":
                throw Unsupported("'startswith' (mapping it onto 'contains' would silently change what the query matches)");
            case "endswith" or "endwith":
                throw Unsupported("'endswith' (mapping it onto 'contains' would silently change what the query matches)");
            case "match" or "regex":
                throw Unsupported("'match' (user-supplied regular expressions are never compiled here — ReDoS, plan §7.1/§7.4)");
            default:
                throw new FilterParseException($"Unexpected token {token} in KQL query; expected a comparison operator.");
        }
    }

    // VALUE := STRING | '@'LITERAL | OBJECT | /0-9 (digits already fall out of OBJECT: the tokenizer has no
    // separate numeric token, so `1700000000` is just a Literal, same as a field name would be).
    private static string ParseValue(TokenReader reader)
    {
        if (reader.HasNext && reader.Peek().Type == TokenType.String)
        {
            return reader.Next().Value!;
        }

        var chain = ParseDotChain(reader);
        if (chain.StartsWith('@'))
        {
            throw Unsupported("'@literal' immediate user value");
        }

        return chain;
    }

    private static bool IsLiteralKeyword(Token token, string keyword) =>
        token.Type == TokenType.Literal && string.Equals(token.Value, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>kql.txt's grammar accepts this construct; 0d's <see cref="FilterNode"/>/<see cref="FilterSource"/>
    /// cannot represent it. Always phrased as "recognized ... but not supported", never "unexpected"/"syntax
    /// error" — the input is valid KQL (plan §7.1).</summary>
    private static FilterParseException Unsupported(string construct) =>
        new($"KQL recognizes {construct} but this version does not support it yet.");
}
