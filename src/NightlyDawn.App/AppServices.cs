using NightlyDawn.App.Timelines;
using NightlyDawn.Core;

namespace NightlyDawn.App;

/// <summary>
/// The App layer's single composition point, filled in by the host executable (<c>src/NightlyDawn.Host</c>) before
/// the Avalonia app starts. Views ask here for contracts from <c>NightlyDawn.Core</c> and never construct a backend
/// themselves, so <c>NightlyDawn.App</c> has no reference to <c>NightlyDawn.Nostr</c> (B9; enforced by
/// <c>ArchitectureTests</c>). Without a host (e.g. a designer preview) the defaults keep the UI usable: the clearly
/// labelled sample timeline source and no screenshot.
/// </summary>
public static class AppServices
{
    public static ITimelineSourceFactory TimelineSourceFactory { get; set; } = new SampleTimelineSourceFactory();

    /// <summary>When set, the main window renders itself to this PNG path once laid out and then exits (spike diagnostics). The host reads <c>NIGHTLYDAWN_SCREENSHOT</c> and puts it here.</summary>
    public static string? ScreenshotPath { get; set; }
}
