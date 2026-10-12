using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NightlyDawn.App.Composing;
using NightlyDawn.App.Keys;
using NightlyDawn.App.Theming;
using NightlyDawn.App.Timelines;
using NightlyDawn.App.Views;

namespace NightlyDawn.App;

public sealed partial class MainWindow : Window
{
    private readonly TextBlock _platformInfo;
    private readonly TextBox _clipboardProbe;
    private readonly TextBlock _clipboardStatus;
    private readonly ComposeViewModel _compose;
    private readonly WorkspaceViewModel _workspace;
    private readonly KeyPanelViewModel _keys;
    private readonly ThemeSwitcherViewModel _theme;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _platformInfo = this.FindControl<TextBlock>("PlatformInfo")!;
        _clipboardProbe = this.FindControl<TextBox>("ClipboardProbe")!;
        _clipboardStatus = this.FindControl<TextBlock>("ClipboardStatus")!;

        // Compose box: built before the workspace below, since each column's actionsFactory (plan §10.1-2)
        // needs a compose view model to hand Reply/Quote targets to. Reaches _workspace back through a Func
        // (sink) rather than a direct reference -- the workspace does not exist yet at this point.
        _compose = new ComposeViewModel(
            publisher: () => AppServices.NotePublisher,
            sink: () => _workspace!.ActiveSink,
            postToUi: action => Dispatcher.UIThread.Post(action));
        this.FindControl<ComposeBoxView>("ComposeBox")!.DataContext = _compose;

        // The workspace starts with one tab and one column (plan §11.4); every further tab/column the user
        // opens is built through CreateColumn below, the single place a column is ever constructed (plan
        // §11.1).
        _workspace = new WorkspaceViewModel(CreateColumn);
        DataContext = _workspace; // WorkspaceView inherits this, same as TimelineColumnView did before 0e-b/L4.

        // Theme switch (L8): the only mutable state it needs is the running Application's variant, so it is
        // independent of the workspace and of the key store. Seeded from the host's choice; the choice made here
        // is not written back to AppServices (see ThemeSwitcherViewModel) and is not persisted (that is L9).
        _theme = new ThemeSwitcherViewModel(
            AppServices.Theme,
            theme => ThemeController.Apply(Application.Current!, theme));
        this.FindControl<ThemeSwitcherView>("ThemeSwitcher")!.Attach(_theme);

        // Keys panel: talks to Core's IKeyStore only (the host constructs the real store). Independent of the workspace.
        _keys = new KeyPanelViewModel(AppServices.KeyStore, postToUi: action => Dispatcher.UIThread.Post(action));
        this.FindControl<KeysPanelView>("KeysPanel")!.Attach(_keys);

        Opened += OnOpened;
        Closed += (_, _) => _workspace.Dispose();
    }

    /// <summary>
    /// The one place a <see cref="TimelineColumnViewModel"/> is built (plan §11.1): the factory and profile
    /// store come from the composition point, never from <c>NightlyDawn.Nostr</c> directly (B9).
    /// <paramref name="column"/>'s <c>actionsFactory</c> closes over the column this call itself produces, not
    /// a field shared across columns -- the required test for this leaf is that column B's row actions report
    /// only to column B, and a factory that closed over one shared field (like the pre-L4 single-column
    /// closure did) would make every column's rows report to whichever column that field pointed at last.
    /// </summary>
    private TimelineColumnViewModel CreateColumn()
    {
        TimelineColumnViewModel? column = null;
        // column is read here only through this closure, invoked lazily (plan §10.1-2's row-build time, well
        // after this method returns) -- the nullable analyzer cannot see that, since it only tracks flow up
        // to this statement, not when the delegate actually runs.
        column = new TimelineColumnViewModel(
            AppServices.TimelineSourceFactory,
            postToUi: action => Dispatcher.UIThread.Post(action),
            profileStore: AppServices.ProfileStore,
            actionsFactory: note => new NoteRowActions(note, () => AppServices.NotePublisher, _compose, column!, column!.ReportStatus));
        return column;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _platformInfo.Text = DescribePlatform();
        _ = _keys.RefreshAsync(); // fire-and-forget: results arrive through PropertyChanged on the UI thread

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
