using System.ComponentModel;
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
using NightlyDawn.App.Persistence;
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
    private readonly LayoutPersistence? _persistence;
    private KeyPanelState _lastKeyState = KeyPanelState.Unavailable;

    /// <summary>How long <see cref="LayoutPersistence.FlushOnClose"/> may hold the window's close for the last
    /// save (L9, plan §32.3: "never block shutdown indefinitely"). A few kilobytes plus two fsyncs is
    /// milliseconds on a healthy disk; two seconds is the line past which the user would rather lose the last
    /// edit than wait.</summary>
    private static readonly TimeSpan CloseFlushTimeout = TimeSpan.FromSeconds(2);

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

        // The workspace starts with one column and one tab (plan §11.4); every further column/tab the user
        // opens is built through CreateTab below, the single place a tab is ever constructed (plan §11.1,
        // inverted for §21.2 / L4b).
        _workspace = new WorkspaceViewModel(CreateTab);
        DataContext = _workspace; // WorkspaceView inherits this, same as TimelineView did before 0e-b/L4.

        // Theme switch (L8): the only mutable state it needs is the running Application's variant, so it is
        // independent of the workspace and of the key store. Seeded from the host's choice; the choice made here
        // is not written back to AppServices (see ThemeSwitcherViewModel). Persisted since L9, below.
        _theme = new ThemeSwitcherViewModel(
            AppServices.Theme,
            theme => ThemeController.Apply(Application.Current!, theme));
        this.FindControl<ThemeSwitcherView>("ThemeSwitcher")!.Attach(_theme);

        // Layout persistence (L9, plan §32): restore here, in the constructor, before the window is ever shown
        // and before LayoutPersistence starts listening -- so the default layout built two statements up is
        // never on screen long enough to be edited, and never saved (plan §32.7's two ordering rules;
        // [[persisted-default-erases-unset]]). The load is a small local file read, synchronous on purpose: a
        // few milliseconds before first paint, against a visible layout swap and an arming race if it were
        // deferred to Opened. Null store = no persistence at all (designer, render tests that do not opt in,
        // host without a data dir), exactly like KeyStore/RemoteImageLoader.
        if (AppServices.AppStateStore is { } appStateStore)
        {
            _persistence = new LayoutPersistence(
                appStateStore,
                _workspace,
                _theme,
                report: problem => Console.Error.WriteLine($"[NightlyDawn] layout: {problem}"));
            _persistence.Restore();
        }

        // Keys panel: talks to Core's IKeyStore only (the host constructs the real store). Independent of the workspace,
        // except for the notification wired below (L5j, plan §18.2 item 4).
        _keys = new KeyPanelViewModel(AppServices.KeyStore, postToUi: action => Dispatcher.UIThread.Post(action));
        _keys.PropertyChanged += OnKeysPropertyChanged;
        this.FindControl<KeysPanelView>("KeysPanel")!.Attach(_keys);

        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>Last save first, then the workspace: the flush must read the columns before Dispose tears them
    /// down. Bounded by <see cref="CloseFlushTimeout"/> so a stuck disk cannot keep the window (and the process
    /// behind it) alive -- an unsaved last edit is the lesser failure.</summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        _persistence?.FlushOnClose(CloseFlushTimeout);
        _persistence?.Dispose();
        _workspace.Dispose();
    }

    /// <summary>
    /// The one place a <see cref="TimelineViewModel"/> is built (plan §11.1, now one tab's content rather than
    /// one column's after §21.2's inversion): the factory and profile store come from the composition point,
    /// never from <c>NightlyDawn.Nostr</c> directly (B9). <paramref name="tab"/>'s <c>actionsFactory</c> closes
    /// over the tab this call itself produces, not a field shared across tabs -- the required test for L4 was
    /// that column B's row actions report only to column B; the same closure now guards tab B instead (plan
    /// §21.2: each physical column still gets its own factory call, one per default tab plus every
    /// <see cref="ColumnViewModel.AddTab"/>).
    /// </summary>
    private TimelineViewModel CreateTab()
    {
        TimelineViewModel? tab = null;
        // tab is read here only through this closure, invoked lazily (plan §10.1-2's row-build time, well
        // after this method returns) -- the nullable analyzer cannot see that, since it only tracks flow up
        // to this statement, not when the delegate actually runs.
        tab = new TimelineViewModel(
            AppServices.TimelineSourceFactory,
            postToUi: action => Dispatcher.UIThread.Post(action),
            profileStore: AppServices.ProfileStore,
            actionsFactory: note => new NoteRowActions(note, () => AppServices.NotePublisher, _compose, tab!, tab!.ReportStatus),
            keyStore: AppServices.KeyStore);
        return tab;
    }

    /// <summary>
    /// The other half of L5j (plan §18.2 item 4): a column that failed with <c>AccountRequiredException</c> has
    /// no way to notice, on its own, that a key just became available -- it only ever looks again when something
    /// calls <see cref="TimelineViewModel.Subscribe"/>. <see cref="KeyPanelViewModel.State"/> is the one
    /// signal this window has for "the key store's answer to 'who is active' just changed", so this is the one
    /// place that turns it into <see cref="WorkspaceViewModel.NotifyAccountUnlocked"/>/
    /// <see cref="WorkspaceViewModel.NotifyAccountLocked"/> calls. <c>_lastKeyState</c> tracks the previous value
    /// by hand because <see cref="PropertyChangedEventArgs"/> carries no old value of its own.
    /// </summary>
    private void OnKeysPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(KeyPanelViewModel.State))
        {
            return;
        }

        var wasUnlocked = _lastKeyState == KeyPanelState.Unlocked;
        _lastKeyState = _keys.State;

        if (_keys.State == KeyPanelState.Unlocked)
        {
            _workspace.NotifyAccountUnlocked();
        }
        else if (wasUnlocked)
        {
            _workspace.NotifyAccountLocked();
        }
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
