using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Keys;

namespace NightlyDawn.App.Views;

public sealed partial class KeysPanelView : UserControl
{
    private readonly TextBlock _keyStatus;
    private readonly Control _generateBox;
    private readonly Control _importBox;
    private readonly Control _unlockBox;
    private readonly Control _unlockedBox;
    private readonly Control _exportBox;
    private readonly TextBox _generatePassphrase;
    private readonly TextBox _generateConfirm;
    private readonly TextBox _importKey;
    private readonly TextBox _importPassphrase;
    private readonly TextBox _unlockPassphrase;
    private readonly TextBox _exportPassphrase;
    private readonly TextBox _exportedKey;
    private readonly TextBlock _pubkeyText;
    private readonly TextBlock _npubText;
    private readonly Button _signOutButton;
    private readonly Control _cancelSignOutButton;

    private KeyPanelViewModel? _keys;

    public KeysPanelView()
    {
        AvaloniaXamlLoader.Load(this);
        _keyStatus = this.FindControl<TextBlock>("KeyStatus")!;
        _generateBox = this.FindControl<Control>("GenerateBox")!;
        _importBox = this.FindControl<Control>("ImportBox")!;
        _unlockBox = this.FindControl<Control>("UnlockBox")!;
        _unlockedBox = this.FindControl<Control>("UnlockedBox")!;
        _exportBox = this.FindControl<Control>("ExportBox")!;
        _generatePassphrase = this.FindControl<TextBox>("GeneratePassphrase")!;
        _generateConfirm = this.FindControl<TextBox>("GenerateConfirm")!;
        _importKey = this.FindControl<TextBox>("ImportKey")!;
        _importPassphrase = this.FindControl<TextBox>("ImportPassphrase")!;
        _unlockPassphrase = this.FindControl<TextBox>("UnlockPassphrase")!;
        _exportPassphrase = this.FindControl<TextBox>("ExportPassphrase")!;
        _exportedKey = this.FindControl<TextBox>("ExportedKey")!;
        _pubkeyText = this.FindControl<TextBlock>("PubkeyText")!;
        _npubText = this.FindControl<TextBlock>("NpubText")!;
        _signOutButton = this.FindControl<Button>("SignOutButton")!;
        _cancelSignOutButton = this.FindControl<Control>("CancelSignOutButton")!;
    }

    /// <summary>Wires the view model and renders the panel once. Called by the host window right after
    /// construction (same place MainWindow used to build these fields directly), so the timing of the
    /// first render and of <see cref="KeyPanelViewModel.PropertyChanged"/> subscription is unchanged.</summary>
    public void Attach(KeyPanelViewModel keys)
    {
        _keys = keys;
        _keys.PropertyChanged += (_, _) => RenderKeyPanel();
        RenderKeyPanel();
    }

    private void RenderKeyPanel()
    {
        if (_keys is null)
        {
            return;
        }

        _keyStatus.Text = _keys.Status;
        _generateBox.IsVisible = _keys.ShowGenerateForm;
        _importBox.IsVisible = _keys.ShowImportForm;
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
        await WithPassphrasesAsync(_generatePassphrase, _generateConfirm, (p, c) => _keys!.GenerateAsync(p, c));

    private async void OnImportKeyClick(object? sender, RoutedEventArgs e) =>
        await WithPassphrasesAsync(_importKey, _importPassphrase, (k, p) => _keys!.ImportAsync(k, p));

    private void OnSwitchToImportClick(object? sender, RoutedEventArgs e) => _keys!.SwitchToImportForm();

    private void OnSwitchToGenerateClick(object? sender, RoutedEventArgs e) => _keys!.SwitchToGenerateForm();

    private async void OnUnlockClick(object? sender, RoutedEventArgs e) =>
        await WithPassphraseAsync(_unlockPassphrase, p => _keys!.UnlockAsync(p));

    private async void OnExportClick(object? sender, RoutedEventArgs e) =>
        await WithPassphraseAsync(_exportPassphrase, p => _keys!.ExportAsync(p));

    private async void OnLockClick(object? sender, RoutedEventArgs e) => await _keys!.LockAsync();

    private async void OnSignOutClick(object? sender, RoutedEventArgs e) => await _keys!.SignOutAsync();

    private void OnCancelSignOutClick(object? sender, RoutedEventArgs e) => _keys!.CancelSignOut();

    // npub is public data, but still copied only on an explicit click (same rule as every clipboard write here).
    private async void OnCopyNpubClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || string.IsNullOrEmpty(_keys!.Npub))
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

    private void OnClearExportClick(object? sender, RoutedEventArgs e) => _keys!.ClearExport();

    // Explicit user action only: nothing reaches the clipboard without this click (Lead's requirement; the clipboard is
    // readable by other processes and may sync to other devices).
    private async void OnCopyExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null || string.IsNullOrEmpty(_keys!.ExportedKey))
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
}
