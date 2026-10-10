using NightlyDawn.Core;

namespace NightlyDawn.Filters;

/// <summary>Ported from StarryEyes/Filters/Parsing/TokenReader.cs: a forward-only cursor over the token list.</summary>
internal sealed class TokenReader(List<Token> tokens)
{
    private int _position;

    public bool HasNext => _position < tokens.Count;

    public Token Peek()
    {
        if (!HasNext)
        {
            throw new FilterParseException("KQL query ends before the expression is complete.");
        }

        return tokens[_position];
    }

    public Token Next() => tokens[_position++];

    /// <summary>Un-consumes the token most recently returned by <see cref="Next"/>. Only used for the single
    /// 2-token lookahead that detects an empty <c>()</c> (<see cref="KqlParser"/>'s PeekSecond) — not a general
    /// rewind API.</summary>
    public void PushBack()
    {
        _position--;
    }

    /// <exception cref="FilterParseException">The next token is missing or not of the expected type.</exception>
    public Token Expect(TokenType type)
    {
        if (!HasNext)
        {
            throw new FilterParseException($"KQL query ends where {Describe(type)} was expected.");
        }

        var token = Next();
        if (token.Type != type)
        {
            throw new FilterParseException($"Unexpected token {token} in KQL query; expected {Describe(type)}.");
        }

        return token;
    }

    private static string Describe(TokenType type) => type switch
    {
        TokenType.OpenParenthesis => "'('",
        TokenType.CloseParenthesis => "')'",
        TokenType.Period => "'.'",
        TokenType.Comma => "','",
        TokenType.Literal => "an identifier",
        TokenType.String => "a quoted string",
        _ => type.ToString(),
    };
}
