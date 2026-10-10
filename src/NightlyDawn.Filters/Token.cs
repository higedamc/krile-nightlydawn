namespace NightlyDawn.Filters;

internal enum TokenType
{
    Literal,
    String,
    Period,
    Comma,
    OpenParenthesis,
    CloseParenthesis,
    Exclamation,
    OperatorAnd,
    OperatorOr,
    OperatorEquals,
    OperatorNotEquals,
    OperatorLessThan,
    OperatorLessThanOrEqual,
    OperatorGreaterThan,
    OperatorGreaterThanOrEqual,
    OperatorContains,
    OperatorIn,
    OperatorPlus,
    OperatorMinus,
    OperatorMultiply,
    OperatorDivide,
}

/// <summary>A lexical token (StarryEyes/Filters/Parsing/Token.cs, trimmed to the tokens KQL v1 needs — see Tokenizer for the ones intentionally dropped).</summary>
internal readonly record struct Token(TokenType Type, string? Value, int Position)
{
    public override string ToString() => Type switch
    {
        TokenType.Literal => $"literal '{Value}'",
        TokenType.String => $"string \"{Value}\"",
        TokenType.Period => "'.'",
        TokenType.Comma => "','",
        TokenType.OpenParenthesis => "'('",
        TokenType.CloseParenthesis => "')'",
        TokenType.Exclamation => "'!'",
        TokenType.OperatorAnd => "'&'",
        TokenType.OperatorOr => "'|'",
        TokenType.OperatorEquals => "'='",
        TokenType.OperatorNotEquals => "'!='",
        TokenType.OperatorLessThan => "'<'",
        TokenType.OperatorLessThanOrEqual => "'<='",
        TokenType.OperatorGreaterThan => "'>'",
        TokenType.OperatorGreaterThanOrEqual => "'>='",
        TokenType.OperatorContains => "'->'",
        TokenType.OperatorIn => "'<-'",
        TokenType.OperatorPlus => "'+'",
        TokenType.OperatorMinus => "'-'",
        TokenType.OperatorMultiply => "'*'",
        TokenType.OperatorDivide => "'/'",
        _ => "unknown token",
    };
}
