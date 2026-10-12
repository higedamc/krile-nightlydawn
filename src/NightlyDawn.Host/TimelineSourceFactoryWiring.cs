using NightlyDawn.Core;
using NightlyDawn.Filters;
using NightlyDawn.Nostr.Timelines;

namespace NightlyDawn.Host;

/// <summary>
/// The composition decision connecting real KQL (<see cref="FilterCompiler"/>, <c>NightlyDawn.Filters</c>) to
/// the Nostr timeline factory (L5i). Lives here, as its own function, so it can be asserted on —
/// <see cref="Program.Main"/> has no seam a test can reach (same reasoning as <see cref="PublisherWiring"/>).
///
/// <para>Mirrors the mutation L1 §12 actually hit: <see cref="NostrTimelineSourceFactory"/>'s public
/// constructor takes <see cref="IFilterCompiler"/> by reference and does not null-check it before handing it to
/// <see cref="NostrFilterQueryCompiler"/> — a composition test built only from each component's own fakes (as
/// <c>NostrTimelineSourceFactoryTests</c>/<c>NostrFilterQueryCompilerTests</c> are) would stay green even if
/// this function's <c>new FilterCompiler()</c> were replaced with <c>null!</c>: nothing throws until a query
/// is actually compiled. Only a test that calls this exact function and then compiles a real query (as
/// <c>TimelineSourceFactoryWiringTests</c> does) catches that.</para>
/// </summary>
internal static class TimelineSourceFactoryWiring
{
    internal static NostrTimelineSourceFactory Create(IReadOnlyCollection<RelayUrl> relays) =>
        new(relays, new FilterCompiler());
}
