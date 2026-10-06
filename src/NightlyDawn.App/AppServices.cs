using NightlyDawn.App.Timelines;
using NightlyDawn.Core;

namespace NightlyDawn.App;

/// <summary>
/// The App layer's single composition point. Views ask here for contracts from <c>NightlyDawn.Core</c>
/// and never construct a backend themselves, so <c>NightlyDawn.App</c> has no reference to
/// <c>NightlyDawn.Nostr</c> (B9; enforced by <c>ArchitectureTests</c>). Leaf 1e-b/1f replaces this default
/// with the relay-backed factory; until then the column shows the clearly labelled sample source.
/// </summary>
internal static class AppServices
{
    public static ITimelineSourceFactory TimelineSourceFactory { get; set; } = new SampleTimelineSourceFactory();
}
