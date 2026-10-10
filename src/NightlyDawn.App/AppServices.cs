using NightlyDawn.App.Timelines;
using NightlyDawn.Core;

namespace NightlyDawn.App;

/// <summary>
/// The App layer's single composition point, filled in by the host executable (<c>src/NightlyDawn.Host</c>) before
/// the Avalonia app starts. <b>The host writes these once, before <c>AppBuilder</c> runs; nothing else sets them.</b>
/// Views ask here for contracts from <c>NightlyDawn.Core</c> and never construct a backend
/// themselves, so <c>NightlyDawn.App</c> has no reference to <c>NightlyDawn.Nostr</c> (B9; enforced by
/// <c>ArchitectureTests</c>). Without a host (e.g. a designer preview) the defaults keep the UI usable: the clearly
/// labelled sample timeline source and no screenshot.
/// </summary>
public static class AppServices
{
    public static ITimelineSourceFactory TimelineSourceFactory { get; set; } = new SampleTimelineSourceFactory();

    /// <summary>When set, the main window renders itself to this PNG path once laid out and then exits (spike diagnostics). The host reads <c>NIGHTLYDAWN_SCREENSHOT</c> and puts it here.</summary>
    public static string? ScreenshotPath { get; set; }

    /// <summary>The key store the host constructed (Core's <see cref="IKeyStore"/>; the App never references <c>NightlyDawn.Keys</c>). Null when the host could not create a data directory; only the Keys panel consumes this, never the timeline.</summary>
    public static IKeyStore? KeyStore { get; set; }

    /// <summary>The profile cache the host constructed (Core's <see cref="IProfileStore"/>; the App never references <c>NightlyDawn.Nostr</c> directly for this either). Null for a designer preview or any caller that has not set it; <see cref="TimelineColumnViewModel"/> treats that the same as "nothing resolved yet" and rows fall back to the pubkey-prefix label.</summary>
    public static IProfileStore? ProfileStore { get; set; }

    /// <summary>The publisher the host constructed (Core's <see cref="INotePublisher"/>; same null-tolerant pattern as <see cref="ProfileStore"/>). Null until a host wires a real implementation -- today that means <c>NightlyDawn.Nostr.Publishing.NotePublisher</c> (L3a), which this leaf does not depend on: both <see cref="App.Composing.ComposeViewModel"/> and <see cref="App.Composing.NoteRowActions"/> read this through a <c>Func&lt;INotePublisher?&gt;</c> so a host wiring it up later needs no new App-layer code.</summary>
    public static INotePublisher? NotePublisher { get; set; }
}
