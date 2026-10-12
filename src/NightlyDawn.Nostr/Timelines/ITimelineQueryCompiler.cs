using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// Turns a column's query text (<see cref="Timeline.KqlQuery"/>) into a lazy, not-yet-run compile step. Leaf 1e-b
/// shipped <c>SimpleTimelineQueryCompiler</c> (a mini language); leaf 1c ported real KQL to
/// <see cref="IFilterCompiler"/> without wiring it up; L5i (this interface's current shape) is the adapter that
/// does.
/// <para><see cref="Compile"/> itself is synchronous and does no I/O — it only parses the query text and checks
/// whether the source needs an account that was not given, both of which <see cref="NostrTimelineSourceFactory"/>
/// needs to know about before it builds anything, so the UI can report a bad query before any relay traffic
/// (matching the old <c>SimpleTimelineQueryCompiler</c> contract). The <em>returned</em> delegate is where any
/// I/O a source needs (currently: a kind:3 fetch for <c>from home</c>) actually happens, lazily, once, on the
/// timeline's first use — see <see cref="NostrTimelineSource"/>.</para>
/// </summary>
public interface ITimelineQueryCompiler
{
    /// <param name="query">The column's raw query text.</param>
    /// <param name="accountPubkey">The signed-in account's own pubkey, or null for <see cref="NostrTimelineSourceFactory.CreateAnonymous"/>.</param>
    /// <exception cref="FilterParseException">The query text is not understood.</exception>
    /// <exception cref="AccountRequiredException">The query's source (<c>from home</c>/<c>from mentions</c>) needs an account and <paramref name="accountPubkey"/> is null.</exception>
    CompiledQuery Compile(string query, string? accountPubkey);
}

/// <summary>What <see cref="ITimelineQueryCompiler.Compile"/> already knows synchronously, plus the deferred
/// step that actually produces a <see cref="CompiledFilter"/>.</summary>
/// <param name="IsAccountBound">True when the query's source needs the signed-in account's identity (<c>from
/// home</c>/<c>from mentions</c>) -- the same check <c>Compile</c> uses to decide whether to throw
/// <see cref="AccountRequiredException"/>, known from the parsed source before any I/O, and exposed here so
/// <see cref="NostrTimelineSourceFactory"/> can stamp it onto the <see cref="ITimelineSource"/> it builds
/// (plan §18.5) instead of the caller guessing from whether it happened to pass an account.</param>
/// <param name="ResolveAsync">Resolved at most once by <see cref="NostrTimelineSource"/>, lazily, on first use.</param>
public sealed record CompiledQuery(bool IsAccountBound, Func<CancellationToken, Task<CompiledFilter>> ResolveAsync);
