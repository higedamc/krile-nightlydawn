using Avalonia;
using NightlyDawn.App;
using NightlyDawn.Keys;

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

    /// <summary>
    /// Theme override (L1): <c>NIGHTLYDAWN_THEME=dark</c> or <c>light</c> pins a variant, <c>system</c> (or unset)
    /// follows the OS. Parsing and the Core-to-Avalonia mapping live in the App layer's
    /// <see cref="NightlyDawn.App.Theming.ThemeController"/>, so the host, the render harness and a future in-app
    /// toggle all switch the theme through one code path.
    /// </summary>
    private static readonly string ThemeEnvVar = NightlyDawn.App.Theming.ThemeController.EnvironmentVariable;

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

        var timelineSourceFactory = TimelineSourceFactoryWiring.Create(relays);
        AppServices.TimelineSourceFactory = timelineSourceFactory;
        AppServices.ProfileStore = timelineSourceFactory.ProfileStore; // Shares the same backend/relay pool (plan §8).
        AppServices.ScreenshotPath = Environment.GetEnvironmentVariable(ScreenshotEnvVar);

        var themeSetting = Environment.GetEnvironmentVariable(ThemeEnvVar);
        if (NightlyDawn.App.Theming.ThemeController.TryParse(themeSetting, out var theme))
        {
            AppServices.Theme = theme;
        }
        else
        {
            // Unrecognised value: say so and follow the OS. Silently treating a typo as "system" would look
            // exactly like a working setting. The value is echoed through Printable so a value carrying newlines
            // or terminal escapes cannot forge a log line or rewrite the operator's terminal (CWE-117).
            Console.Error.WriteLine($"[NightlyDawn] {ThemeEnvVar}=\"{Printable(themeSetting)}\" is not one of light|dark|system; following the OS theme.");
        }

        Console.Error.WriteLine($"[NightlyDawn] theme: {AppServices.Theme.ToString().ToLowerInvariant()}");
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

        // Publishing shares the timeline's backend and relay pool (L3c). No key store means no publisher: the App
        // layer reads AppServices.NotePublisher through a Func<INotePublisher?> and reports publishing as
        // unavailable, which is a truer thing to show than a signing failure after the user has typed.
        AppServices.NotePublisher = PublisherWiring.Create(timelineSourceFactory, AppServices.KeyStore);
        Console.Error.WriteLine($"[NightlyDawn] publishing: {(AppServices.NotePublisher is null ? "unavailable (no key store)" : "ready")}");

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            timelineSourceFactory.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Renders an environment-supplied value safe to write to stderr: every control character (newlines, the ESC
    /// that starts a terminal escape sequence, NUL) becomes a visible <c>?</c>. Diagnostics quote values the
    /// operator set, and an operator reading them in a terminal should not be able to have their terminal driven
    /// or a log line forged by what was in the variable (CWE-117).
    /// </summary>
    private static string Printable(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Select(c => char.IsControl(c) ? '?' : c));
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
