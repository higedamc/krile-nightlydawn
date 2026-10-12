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

    /// <summary>The profile cache the host constructed (Core's <see cref="IProfileStore"/>; the App never references <c>NightlyDawn.Nostr</c> directly for this either). Null for a designer preview or any caller that has not set it; <see cref="TimelineViewModel"/> treats that the same as "nothing resolved yet" and rows fall back to the pubkey-prefix label.</summary>
    public static IProfileStore? ProfileStore { get; set; }

    /// <summary>The publisher the host constructed (Core's <see cref="INotePublisher"/>; same null-tolerant pattern as <see cref="ProfileStore"/>). Null until a host wires a real implementation -- today that means <c>NightlyDawn.Nostr.Publishing.NotePublisher</c> (L3a), which this leaf does not depend on: both <see cref="App.Composing.ComposeViewModel"/> and <see cref="App.Composing.NoteRowActions"/> read this through a <c>Func&lt;INotePublisher?&gt;</c> so a host wiring it up later needs no new App-layer code.</summary>
    public static INotePublisher? NotePublisher { get; set; }

    /// <summary>The image loader the host constructed (Core's <see cref="IRemoteImageLoader"/>; today
    /// <c>NightlyDawn.Storage.RemoteImageLoader</c>, L6a). <b>Null means no avatar is fetched, for anyone</b>: the
    /// row view checks this before asking <see cref="Avatars.AvatarImageCache"/> for anything, so switching
    /// avatars off is this one assignment, not a search through the views. The default is to load avatars for
    /// every author, not only followed ones (plan §26.4, decided by Lead and reported to ???) -- the beacon
    /// that <c>RemoteImageLoader</c>'s remarks describe is accepted as the price of reproducing the fork's row.
    /// Null for a designer preview, for any test that does not set it, and for a host without a data
    /// directory (the disk cache has nowhere to live).</summary>
    public static IRemoteImageLoader? RemoteImageLoader { get; set; }

    /// <summary>The layout store the host constructed (Core's <see cref="IAppStateStore"/>; today
    /// <c>NightlyDawn.Storage.JsonAppStateStore</c> on the data directory, L9). Same null-tolerant pattern as
    /// <see cref="KeyStore"/> and <see cref="RemoteImageLoader"/>: <b>null means no persistence at all</b> --
    /// <see cref="MainWindow"/> neither restores nor saves, the built-in one-column layout is used for the
    /// process lifetime, and nothing is written anywhere. That is what a designer preview, a host without a data
    /// directory, and every render test that does not set this get. The <em>store</em> is what goes here, not a
    /// loaded <see cref="AppState"/>: the restore (and so the untrusted-file handling in the store's
    /// <c>LoadAsync</c>) runs inside the App layer, <see cref="Persistence.LayoutPersistence"/>, so a test that
    /// points this at a real file on disk exercises the same parse-and-fall-back path the shipped host does
    /// (plan §32.7).</summary>
    public static IAppStateStore? AppStateStore { get; set; }

    /// <summary>The theme variant the host chose, read once by <see cref="App.Initialize"/> (L1). Defaults to
    /// <see cref="AppTheme.System"/> = follow the OS, which is also what a designer preview and any host that does
    /// not set it get. The host reads <c>NIGHTLYDAWN_THEME</c>; see <see cref="Theming.ThemeController"/>.</summary>
    public static AppTheme Theme { get; set; } = AppTheme.System;
}
