using NightlyDawn.Core;

namespace NightlyDawn.App.Search;

/// <summary>
/// Turns a <see cref="FilterAst"/> back into KQL text -- the inverse of <see cref="IFilterCompiler.Parse"/>.
/// The only caller is <see cref="SearchFlipViewModel.PinToTab"/> (plan §37.2 item 11, L7b): pinning a
/// <see cref="SearchMode.CurrentTab"/> result ANDs the keyword expression onto the current tab's own query
/// (plan §41 item ②), and the only KQL text a tab's own query exists as is what the person typed or a previous
/// pin generated -- there is no AST held anywhere to reuse, so it has to be reproduced as text. Covers exactly
/// what <see cref="NightlyDawn.Filters.KqlParser"/> (via <see cref="IFilterCompiler.Parse"/>) can produce in a
/// <see cref="FilterAst"/>: it is not a general KQL pretty-printer and never needs to parse anything itself.
/// </summary>
internal static class KqlWriter
{
    public static string Write(FilterAst ast) =>
        ast.Where is null ? $"from {WriteSource(ast.Source)} where ()" : $"from {WriteSource(ast.Source)} where {WriteNode(ast.Where)}";

    private static string WriteSource(FilterSource source) => source.Kind switch
    {
        FilterSourceKind.Home => "home",
        FilterSourceKind.Mentions => "mentions",
        FilterSourceKind.User => $"user({Quote(source.Argument!)})",
        FilterSourceKind.List => $"list({Quote(source.Argument!)})",
        FilterSourceKind.Search => $"search({Quote(source.Argument!)})",
        FilterSourceKind.Relay => $"relay({Quote(source.Argument!)})",
        FilterSourceKind.Kind => $"kind({Quote(source.Argument!)})",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source.Kind, "Unknown FilterSourceKind."),
    };

    // Every operand is parenthesized unconditionally, including a single comparison -- KQL's precedence
    // (OR loosest, then AND, then NOT: KqlParser.ParseOr/ParseAnd/ParseNot) means an unparenthesized "a && b || c"
    // would silently regroup once re-parsed; always wrapping costs nothing a person reads back (this text is
    // never shown, only re-parsed by Subscribe) and needs no precedence bookkeeping here to get right.
    private static string WriteNode(FilterNode node) => node switch
    {
        FilterComparison(var field, var op, var value) => $"{field} {WriteOperator(op)} {Quote(value)}",
        FilterNot(var operand) => $"!({WriteNode(operand)})",
        FilterAnd(var left, var right) => $"({WriteNode(left)}) && ({WriteNode(right)})",
        FilterOr(var left, var right) => $"({WriteNode(left)}) || ({WriteNode(right)})",
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown FilterNode."),
    };

    private static string WriteOperator(FilterComparisonOperator op) => op switch
    {
        FilterComparisonOperator.Equals => "=",
        FilterComparisonOperator.NotEquals => "!=",
        FilterComparisonOperator.Contains => "contains",
        FilterComparisonOperator.GreaterThan => ">",
        FilterComparisonOperator.LessThan => "<",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown FilterComparisonOperator."),
    };

    // The inverse of Tokenizer.ReadQuotedString, which only ever unescapes \" and \\ -- escaping exactly those
    // two characters, in this order (backslash first, so a value ending in a literal backslash does not swallow
    // the closing quote's own escape), is what makes Write(Parse(text)) reproduce an equivalent query.
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
