using NightlyDawn.Core;

namespace NightlyDawn.Filters;

/// <summary>
/// Ported from StarryEyes/Filters/Parsing/Tokenizer.cs. Kept: the character-splitter scan for literals/numbers,
/// quoted strings with backslash escaping, and the two-character operators (<c>&amp;&amp;</c>, <c>||</c>, <c>==</c>,
/// <c>!=</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>-&gt;</c>, <c>&lt;-</c>). Dropped: <c>[</c>/<c>]</c> (StarryEyes' set
/// literals — v1 has no set-valued field) and <c>:</c> (StarryEyes' <c>source: "arg"</c> syntax — kql.txt and plan
/// §4's own examples use <c>source("arg")</c> instead, so parentheses already do this job and a second,
/// unreachable-in-this-grammar token would just be dead code).
/// </summary>
internal static class Tokenizer
{
    /// <summary>Queries are a tab's persisted setting and get re-parsed on every startup, so this is an input a hostile config file (synced, shared, or restored from a backup) can control. 8 KiB is far beyond any real query but bounds tokenizer work before a single token is produced (plan §7.4).</summary>
    public const int MaxQueryLength = 8192;

    public static List<Token> Tokenize(string kql)
    {
        if (kql.Length > MaxQueryLength)
        {
            throw new FilterParseException($"KQL query is {kql.Length} characters, longer than the {MaxQueryLength}-character limit.");
        }

        var tokens = new List<Token>();
        var i = 0;
        while (i < kql.Length)
        {
            var c = kql[i];
            switch (c)
            {
                case ' ' or '\t' or '\r' or '\n':
                    i++;
                    continue;
                case '(':
                    tokens.Add(new Token(TokenType.OpenParenthesis, null, i));
                    i++;
                    continue;
                case ')':
                    tokens.Add(new Token(TokenType.CloseParenthesis, null, i));
                    i++;
                    continue;
                case '.':
                    tokens.Add(new Token(TokenType.Period, null, i));
                    i++;
                    continue;
                case ',':
                    tokens.Add(new Token(TokenType.Comma, null, i));
                    i++;
                    continue;
                case '&':
                    tokens.Add(new Token(TokenType.OperatorAnd, null, i));
                    i += CheckNext(kql, i, '&') ? 2 : 1;
                    continue;
                case '|':
                    tokens.Add(new Token(TokenType.OperatorOr, null, i));
                    i += CheckNext(kql, i, '|') ? 2 : 1;
                    continue;
                case '=':
                    tokens.Add(new Token(TokenType.OperatorEquals, null, i));
                    i += CheckNext(kql, i, '=') ? 2 : 1;
                    continue;
                case '!':
                    if (CheckNext(kql, i, '='))
                    {
                        tokens.Add(new Token(TokenType.OperatorNotEquals, null, i));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenType.Exclamation, null, i));
                        i++;
                    }

                    continue;
                case '<':
                    if (CheckNext(kql, i, '='))
                    {
                        tokens.Add(new Token(TokenType.OperatorLessThanOrEqual, null, i));
                        i += 2;
                    }
                    else if (CheckNext(kql, i, '-'))
                    {
                        tokens.Add(new Token(TokenType.OperatorIn, null, i));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenType.OperatorLessThan, null, i));
                        i++;
                    }

                    continue;
                case '>':
                    if (CheckNext(kql, i, '='))
                    {
                        tokens.Add(new Token(TokenType.OperatorGreaterThanOrEqual, null, i));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenType.OperatorGreaterThan, null, i));
                        i++;
                    }

                    continue;
                case '-':
                    if (CheckNext(kql, i, '>'))
                    {
                        tokens.Add(new Token(TokenType.OperatorContains, null, i));
                        i += 2;
                    }
                    else
                    {
                        tokens.Add(new Token(TokenType.OperatorMinus, null, i));
                        i++;
                    }

                    continue;
                case '+':
                    tokens.Add(new Token(TokenType.OperatorPlus, null, i));
                    i++;
                    continue;
                case '*':
                    tokens.Add(new Token(TokenType.OperatorMultiply, null, i));
                    i++;
                    continue;
                case '/':
                    tokens.Add(new Token(TokenType.OperatorDivide, null, i));
                    i++;
                    continue;
                case '"':
                {
                    var start = i;
                    var value = ReadQuotedString(kql, ref i);
                    tokens.Add(new Token(TokenType.String, value, start));
                    continue;
                }

                default:
                {
                    var start = i;
                    while (i < kql.Length && !IsSplitter(kql[i]))
                    {
                        i++;
                    }

                    if (i == start)
                    {
                        throw new FilterParseException($"Unexpected character '{c}' at position {i} in KQL query.");
                    }

                    tokens.Add(new Token(TokenType.Literal, kql[start..i], start));
                    continue;
                }
            }
        }

        return tokens;
    }

    private static bool IsSplitter(char c) => "&|<>()+-*/.,=!\" \t\r\n".Contains(c);

    private static bool CheckNext(string kql, int i, char expected) => i + 1 < kql.Length && kql[i + 1] == expected;

    /// <exception cref="FilterParseException">The quoted string is never closed before the query ends.</exception>
    private static string ReadQuotedString(string kql, ref int i)
    {
        var openedAt = i;
        i++; // skip opening quote
        var sb = new System.Text.StringBuilder();
        while (i < kql.Length)
        {
            var c = kql[i];
            if (c == '\\' && i + 1 < kql.Length && (kql[i + 1] == '"' || kql[i + 1] == '\\'))
            {
                sb.Append(kql[i + 1]);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                i++;
                return sb.ToString();
            }

            sb.Append(c);
            i++;
        }

        throw new FilterParseException($"Quoted string starting at position {openedAt} is never closed.");
    }
}
