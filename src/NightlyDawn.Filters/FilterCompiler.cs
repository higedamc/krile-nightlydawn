using NightlyDawn.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("NightlyDawn.Filters.Tests")]

namespace NightlyDawn.Filters;

/// <summary>KQL (plan §4, kql.txt) parsing and relay-filter compilation, ported from StarryEyes/Filters/Parsing.
/// <see cref="Parse"/> and <see cref="Compile"/> are deliberately separate steps (matching <see cref="IFilterCompiler"/>):
/// Parse only needs the query text, Compile optionally needs an account pubkey and a resolved follow list to
/// compile <c>from home</c>/<c>from mentions</c> (NightlyDawn.Nostr resolves the kind:3 fetch and passes the
/// result in; this project does no I/O). <c>from list</c> stays unsupported regardless (plan §7.3, L5i).</summary>
public sealed class FilterCompiler : IFilterCompiler
{
    /// <exception cref="FilterParseException">The input is not valid KQL.</exception>
    public FilterAst Parse(string kql) => KqlParser.Parse(kql);

    /// <exception cref="UnsupportedFilterFieldException">The AST uses a field or source this version recognizes but cannot compile yet (plan §1 item B, §7.3) -- including <c>from home</c>/<c>from mentions</c> without the matching parameter.</exception>
    /// <exception cref="FilterParseException">A source argument (npub/hex, kind number, relay URL) or comparison value is malformed (plan §7.4).</exception>
    public CompiledFilter Compile(FilterAst ast, string? accountPubkey = null, IReadOnlyList<string>? resolvedFollows = null) =>
        KqlCompiler.Compile(ast, accountPubkey, resolvedFollows);
}
