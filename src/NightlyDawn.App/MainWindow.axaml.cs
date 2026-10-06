using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App;

public sealed partial class MainWindow : Window
{
    private readonly TextBlock _platformInfo;
    private readonly TextBox _clipboardProbe;
    private readonly TextBlock _clipboardStatus;
    private readonly TimelineColumnViewModel _timeline;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _platformInfo = this.FindControl<TextBlock>("PlatformInfo")!;
        _clipboardProbe = this.FindControl<TextBox>("ClipboardProbe")!;
        _clipboardStatus = this.FindControl<TextBlock>("ClipboardStatus")!;

        // One read-only column for now (1e). The factory comes from the composition point, never from NightlyDawn.Nostr (B9).
        _timeline = new TimelineColumnViewModel(
            AppServices.TimelineSourceFactory,
            postToUi: action => Dispatcher.UIThread.Post(action));
        DataContext = _timeline;

        Opened += OnOpened;
        Closed += (_, _) => _timeline.Dispose();
    }

    private void OnSubscribeClick(object? sender, RoutedEventArgs e) => _timeline.Subscribe();

    private void OnOpened(object? sender, EventArgs e)
    {
        _platformInfo.Text = DescribePlatform();

        // Spike helper: when the host set a screenshot path (from NIGHTLYDAWN_SCREENSHOT), render the window
        // offscreen to that file after it has laid out, then exit. Works without a display-server screenshot
        // tool (headless Macs, Wayland compositors without a grabber).
        var screenshotPath = AppServices.ScreenshotPath;
        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            return;
        }

        // Let the first layout/render pass finish before rasterizing.
        Dispatcher.UIThread.Post(() => SaveScreenshotAndClose(screenshotPath), DispatcherPriority.Background);
    }

    private void SaveScreenshotAndClose(string path)
    {
        try
        {
            var scale = RenderScaling;
            var size = new PixelSize(
                Math.Max(1, (int)Math.Round(Bounds.Width * scale)),
                Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(this);
            bitmap.Save(path, new PngBitmapEncoderOptions());
            Console.Error.WriteLine($"[NightlyDawn] screenshot saved: {path} ({size.Width}x{size.Height})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[NightlyDawn] screenshot failed: {ex.Message}");
        }

        Close();
    }

    private string DescribePlatform()
    {
        var avaloniaVersion = typeof(AppBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var wayland = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
        var x11 = Environment.GetEnvironmentVariable("DISPLAY");
        var backendOverride = Environment.GetEnvironmentVariable("NIGHTLYDAWN_BACKEND");
        var screen = Screens.ScreenFromWindow(this);

        return string.Join('\n',
            $"OS           : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $".NET         : {RuntimeInformation.FrameworkDescription}",
            $"Avalonia     : {avaloniaVersion}",
            $"WAYLAND_DISPLAY / DISPLAY : {wayland ?? "(unset)"} / {x11 ?? "(unset)"}",
            $"NIGHTLYDAWN_BACKEND       : {backendOverride ?? "(unset = detect)"}",
            $"Render scaling: {RenderScaling:0.##}  Screen: {screen?.Bounds.Width}x{screen?.Bounds.Height} @ {screen?.Scaling:0.##}",
            $"Window (DIP) : {Bounds.Width:0}x{Bounds.Height:0}");
    }

    // Both handlers are `async void` (event handlers), so an unhandled exception would take the
    // process down. This window exists to *report* platform failures, so every clipboard error is
    // caught and shown in the status line instead — the Wayland backend is new upstream code.
    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                _clipboardStatus.Text = "Clipboard: not available on this platform";
                return;
            }

            var text = _clipboardProbe.Text ?? string.Empty;
            await clipboard.SetTextAsync(text);
            _clipboardStatus.Text = $"Copied {text.Length} chars at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _clipboardStatus.Text = $"Copy failed — {ex.GetType().Name}: {ex.Message}";
        }
    }

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                _clipboardStatus.Text = "Clipboard: not available on this platform";
                return;
            }

            var text = await clipboard.TryGetTextAsync();
            _clipboardProbe.Text = text ?? string.Empty;
            _clipboardStatus.Text = text is null
                ? "Pasted: clipboard has no text"
                : $"Pasted {text.Length} chars at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _clipboardStatus.Text = $"Paste failed — {ex.GetType().Name}: {ex.Message}";
        }
    }
}
