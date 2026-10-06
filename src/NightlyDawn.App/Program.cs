using Avalonia;

namespace NightlyDawn.App;

internal static class Program
{
    /// <summary>
    /// Backend override for the Omarchy spike: <c>NIGHTLYDAWN_BACKEND=wayland</c> forces the
    /// native Wayland backend, <c>x11</c> forces X11 (XWayland under Hyprland). Unset = detect:
    /// Wayland when a compositor is reachable, otherwise X11. Ignored on macOS.
    /// </summary>
    private const string BackendEnvVar = "NIGHTLYDAWN_BACKEND";

    [STAThread]
    private static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the Avalonia designer / previewer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
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
