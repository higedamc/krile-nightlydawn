using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NightlyDawn.App.Keys;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App;

public sealed partial class MainWindow : Window
{
    private readonly TextBlock _platformInfo;
    private readonly TextBox _clipboardProbe;
    private readonly TextBlock _clipboardStatus;
    private readonly TimelineColumnViewModel _timeline;
    private readonly KeyPanelViewModel _keys;
    private readonly TextBlock _keyStatus;
    private readonly Control _generateBox;
    private readonly Control _unlockBox;
    private readonly Control _unlockedBox;
    private readonly Control _exportBox;
    private readonly TextBox _generatePassphrase;
    private readonly TextBox _generateConfirm;
    private readonly TextBox _unlockPassphrase;
    private readonly TextBox _exportPassphrase;
    private readonly TextBox _exportedKey;
    private readonly TextBlock _pubkeyText;
    private readonly TextBlock _npubText;
    private readonly Button _signOutButton;
    private readonly Control _cancelSignOutButton;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _platformInfo = this.FindControl<TextBlock>("PlatformInfo")!;
        _clipboardProbe = this.FindControl<TextBox>("ClipboardProbe")!;
        _clipboardStatus = this.FindControl<TextBlock>("ClipboardStatus")!;

        // One read-only column for now (1e). The factory and profile store come from the composition point,
        // never from NightlyDawn.Nostr directly (B9).
        _timeline = new TimelineColumnViewModel(
            AppServices.TimelineSourceFactory,
            postToUi: action => Dispatcher.UIThread.Post(action),
            profileStore: AppServices.ProfileStore);
        DataContext = _timeline;

        // Keys panel: talks to Core's IKeyStore only (the host constructs the real store). Independent of the timeline.
        _keys = new KeyPanelViewModel(AppServices.KeyStore, postToUi: action => Dispatcher.UIThread.Post(action));
        _keyStatus = this.FindControl<TextBlock>("KeyStatus")!;
        _generateBox = this.FindControl<Control>("GenerateBox")!;
        _unlockBox = this.FindControl<Control>("UnlockBox")!;
        _unlockedBox = this.FindControl<Control>("UnlockedBox")!;
        _exportBox = this.FindControl<Control>("ExportBox")!;
        _generatePassphrase = this.FindControl<TextBox>("GeneratePassphrase")!;
        _generateConfirm = this.FindControl<TextBox>("GenerateConfirm")!;
        _unlockPassphrase = this.FindControl<TextBox>("UnlockPassphrase")!;
        _exportPassphrase = this.FindControl<TextBox>("ExportPassphrase")!;
        _exportedKey = this.FindControl<TextBox>("ExportedKey")!;
        _pubkeyText = this.FindControl<TextBlock>("PubkeyText")!;
        _npubText = this.FindControl<TextBlock>("NpubText")!;
        _signOutButton = this.FindControl<Button>("SignOutButton")!;
        _cancelSignOutButton = this.FindControl<Control>("CancelSignOutButton")!;
        _keys.PropertyChanged += (_, _) => RenderKeyPanel();
        RenderKeyPanel();

        Opened += OnOpened;
        Closed += (_, _) => _timeline.Dispose();
    }

    private void RenderKeyPanel()
    {
        _keyStatus.Text = _keys.Status;
        _generateBox.IsVisible = _keys.ShowGenerate;
        _unlockBox.IsVisible = _keys.ShowUnlock;
        _unlockedBox.IsVisible = _keys.ShowUnlocked;
        _exportBox.IsVisible = _keys.HasExport;
        _pubkeyText.Text = _keys.PubkeyHex ?? string.Empty;
        _npubText.Text = _keys.Npub ?? string.Empty;
        _exportedKey.Text = _keys.ExportedKey ?? string.Empty;
        _signOutButton.Content = _keys.SignOutArmed ? "Confirm: delete the stored key" : "Sign out (deletes the stored key from this device)";
        _cancelSignOutButton.IsVisible = _keys.SignOutArmed;
    }

    /// <summary>
    /// Takes the passphrase out of a TextBox into a char buffer, clears the box, hands the buffer to <paramref name="action"/>
    /// as ReadOnlyMemory, and zeroes it afterwards. Honest limit: Avalonia's TextBox holds its text as an immutable string
    /// that stays in managed memory until collected; this keeps our own copies short-lived, no more.
    /// </summary>
    private static async Task WithPassphraseAsync(TextBox box, Func<ReadOnlyMemory<char>, Task> action)
    {
        var buffer = (box.Text ?? string.Empty).ToCharArray();
        box.Text = string.Empty;
        try
        {
            await action(buffer);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static async Task WithPassphrasesAsync(TextBox first, TextBox second, Func<ReadOnlyMemory<char>, ReadOnlyMemory<char>, Task> action)
    {
        var a = (first.Text ?? string.Empty).ToCharArray();
        var b = (second.Text ?? string.Empty).ToCharArray();
        first.Text = string.Empty;
        second.Text = string.Empty;
        try
        {
            await action(a, b);
        }
        finally
        {
            Array.Clear(a);
            Array.Clear(b);
        }
    }

    // async void handlers: every failure is caught inside the view model and surfaced as a status line, never thrown here.
    private async void OnGenerateKeyClick(object? sender, RoutedEventArgs e) =>
        await WithPassphrasesAsync(_generatePassphrase, _generateConfirm, (p, c) => _keys.GenerateAsync(p, c));

    private async void OnUnlockClick(object? sender, RoutedEventArgs e) =>
        await WithPassphraseAsync(_unlockPassphrase, p => _keys.UnlockAsync(p));

    private async void OnExportClick(object? sender, RoutedEventArgs e) =>
        await WithPassphraseAsync(_exportPassphrase, p => _keys.ExportAsync(p));

    private async void OnLockClick(object? sender, RoutedEventArgs e) => await _keys.LockAsync();

    private async void OnSignOutClick(object? sender, RoutedEventArgs e) => await _keys.SignOutAsync();

    private void OnCancelSignOutClick(object? sender, RoutedEventArgs e) => _keys.CancelSignOut();

    // npub is public data, but still copied only on an explicit click (same rule as every clipboard write here).
    private async void OnCopyNpubClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || string.IsNullOrEmpty(_keys.Npub))
            {
                return;
            }

            await clipboard.SetTextAsync(_keys.Npub);
            _keyStatus.Text = "npub copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _keyStatus.Text = $"Copy failed ({ex.GetType().Name}).";
        }
    }

    private void OnClearExportClick(object? sender, RoutedEventArgs e) => _keys.ClearExport();

    // Explicit user action only: nothing reaches the clipboard without this click (Lead's requirement; the clipboard is
    // readable by other processes and may sync to other devices).
    private async void OnCopyExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || string.IsNullOrEmpty(_keys.ExportedKey))
            {
                return;
            }

            await clipboard.SetTextAsync(_keys.ExportedKey);
            _keyStatus.Text = "Encrypted key copied to the clipboard.";
        }
        catch (Exception ex)
        {
            _keyStatus.Text = $"Copy failed ({ex.GetType().Name}).";
        }
    }

    private void OnSubscribeClick(object? sender, RoutedEventArgs e) => _timeline.Subscribe();

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
