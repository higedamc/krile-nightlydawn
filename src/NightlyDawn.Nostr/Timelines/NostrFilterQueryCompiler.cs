using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// The real <see cref="ITimelineQueryCompiler"/>: <see cref="IFilterCompiler"/> (KQL parsing and relay-filter
/// compilation, <c>NightlyDawn.Filters</c>) plus <see cref="IContactListSource"/> (the kind:3 fetch
/// <c>IFilterCompiler</c> cannot do itself, since it does no I/O). Replaces
/// <c>SimpleTimelineQueryCompiler</c>'s mini language now that KQL is wired all the way through (L5i).
/// </summary>
public sealed class NostrFilterQueryCompiler(IFilterCompiler filterCompiler, IContactListSource contactListSource) : ITimelineQueryCompiler
{
    /// <exception cref="FilterParseException">The query text is not understood.</exception>
    /// <exception cref="AccountRequiredException">The source is <c>from home</c> or <c>from mentions</c> and <paramref name="accountPubkey"/> is null.</exception>
    /// <exception cref="UnsupportedFilterFieldException">The source or a <c>where</c> field is recognized but not compilable yet (e.g. <c>from list</c>) -- thrown here, synchronously, same as the two exceptions above: every source other than <c>from home</c> needs no I/O to know this.</exception>
    public CompiledQuery Compile(string query, string? accountPubkey)
    {
        var ast = filterCompiler.Parse(query);
        var isAccountBound = RequiresAccount(ast.Source.Kind);

        if (accountPubkey is null && isAccountBound)
        {
            throw new AccountRequiredException(ast.Source.Kind);
        }

        if (ast.Source.Kind == FilterSourceKind.Home)
        {
            // The only source that needs I/O: the follow list is not known yet, so compiling has to wait for it.
            // Deferred to the returned delegate rather than done here so a bad query still fails synchronously above.
            return new CompiledQuery(isAccountBound, cancellationToken => ResolveHomeAsync(ast, accountPubkey!, cancellationToken));
        }

        // Every other source (including `from mentions`, which only needs the pubkey it already has) compiles
        // with no I/O, so there is nothing to defer.
        var compiled = filterCompiler.Compile(ast, accountPubkey);
        return new CompiledQuery(isAccountBound, _ => Task.FromResult(compiled));
    }

    private static bool RequiresAccount(FilterSourceKind kind) => kind is FilterSourceKind.Home or FilterSourceKind.Mentions;

    private async Task<CompiledFilter> ResolveHomeAsync(FilterAst ast, string accountPubkey, CancellationToken cancellationToken)
    {
        var follows = await contactListSource.FetchContactListAsync(accountPubkey, cancellationToken).ConfigureAwait(false);
        return filterCompiler.Compile(ast, accountPubkey, follows);
    }
}
