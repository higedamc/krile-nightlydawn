using Avalonia;
using NightlyDawn.App;
using NightlyDawn.Keys;
using NightlyDawn.Nostr.Timelines;

namespace NightlyDawn.Host;

/// <summary>
/// Entry point and composition root. Everything environment-specific is read here and handed to the App layer
/// through <see cref="AppServices"/>: which relays to read, which Avalonia backend to use, whether to render a
/// screenshot and exit. The App never reads these itself and never sees <c>NightlyDawn.Nostr</c> types (B9).
/// </summary>
internal static class Program
{
    /// <summary>
    /// Backend override for the Omarchy spike: <c>NIGHTLYDAWN_BACKEND=wayland</c> forces the native Wayland
    /// backend, <c>x11</c> forces X11 (XWayland under Hyprland). Unset = detect: Wayland when a compositor is
    /// reachable, otherwise X11. Ignored on macOS.
    /// </summary>
    private const string BackendEnvVar = "NIGHTLYDAWN_BACKEND";

    /// <summary>
    /// Spike helper: <c>NIGHTLYDAWN_SCREENSHOT=/path/to.png</c> makes the main window render itself offscreen to
    /// that file once laid out, then exit. Works without a display-server screenshot tool.
    /// </summary>
    private const string ScreenshotEnvVar = "NIGHTLYDAWN_SCREENSHOT";

    [STAThread]
    private static int Main(string[] args)
    {
        IReadOnlyList<Core.RelayUrl> relays;
        try
        {
            relays = RelayConfiguration.FromEnvironment();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[NightlyDawn] {ex.Message}");
            Console.Error.WriteLine($"[NightlyDawn] Set {RelayConfiguration.EnvironmentVariable} to a comma-separated list of wss:// relay URLs, or unset it for the defaults ({string.Join(", ", RelayConfiguration.DefaultRelays)}).");
            return 2;
        }

        var timelineSourceFactory = new NostrTimelineSourceFactory(relays);
        AppServices.TimelineSourceFactory = timelineSourceFactory;
        AppServices.ProfileStore = timelineSourceFactory.ProfileStore; // Shares the same backend/relay pool (plan §8).
        AppServices.ScreenshotPath = Environment.GetEnvironmentVariable(ScreenshotEnvVar);
        Console.Error.WriteLine($"[NightlyDawn] relays: {string.Join(", ", relays.Select(r => r.Value))}");

        // Local NIP-49 key store (leaf 1b), wired here so the App only ever sees Core's IKeyStore. The timeline
        // never consults it: reading public notes needs no key, and the Keys panel is the only consumer.
        try
        {
            var dataDir = AppDataPaths.ResolveFromEnvironment();
            var keyFile = AppDataPaths.EnsureLocalKeyFile(dataDir);
            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(keyFile));
            Console.Error.WriteLine($"[NightlyDawn] data dir: {dataDir}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No data directory means no key store; the timeline still works. The panel shows the store as unavailable.
            Console.Error.WriteLine($"[NightlyDawn] key store unavailable: {ex.GetType().Name}");
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            timelineSourceFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    // Also used by the Avalonia designer / previewer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (!OperatingSystem.IsLinux())
        {
            return builder;
        }

        return Environment.GetEnvironmentVariable(BackendEnvVar)?.Trim().ToLowerInvariant() switch
        {
            "x11" => builder,
            "wayland" => builder.UseWayland(),
            _ => builder.UseWaylandWithFallback(),
        };
    }
}
