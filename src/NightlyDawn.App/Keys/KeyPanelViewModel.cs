using System.ComponentModel;
using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.Keys;

public enum KeyPanelState
{
    /// <summary>The host provided no key store (e.g. the data directory could not be created). The timeline still works.</summary>
    Unavailable,
    NoKey,
    /// <summary>An encrypted key is stored but no passphrase has been given this session.</summary>
    Locked,
    Unlocked,
}

/// <summary>
/// State machine behind the "Keys" panel (goal ②'s UI half). Talks only to Core's <see cref="IKeyStore"/>; never sees
/// key material — the store returns a <see cref="SignerDescriptor"/> (pubkey + kind) or NIP-49 ciphertext, nothing
/// else. Passphrases arrive as <see cref="ReadOnlyMemory{T}"/> over a buffer the view clears after each call; this
/// class never copies them into strings and never logs them. Status text names exception <em>types</em> only.
/// The timeline column does not depend on this panel in any way: reading public notes needs no key.
/// </summary>
public sealed class KeyPanelViewModel : INotifyPropertyChanged
{
    public const string GeneratePassphraseWarning =
        "This passphrase is the only thing protecting the key. NIP-49 recommends never publishing an ncryptsec: a weak passphrase can be brute-forced (about 100 ms per guess at the default cost).";

    public const string ExportClipboardWarning =
        "This is the encrypted form (ncryptsec); it needs your passphrase to use. Copying puts it on the system clipboard, which other apps on this account and Universal Clipboard can read. Nothing is copied until you press Copy.";

    private readonly IKeyStore? _store;
    private readonly Action<Action> _postToUi;
    private KeyPanelState _state = KeyPanelState.Unavailable;
    private string _status = string.Empty;
    private string? _pubkeyHex;
    private string? _exportedKey;
    private bool _isBusy;

    public KeyPanelViewModel(IKeyStore? store, Action<Action> postToUi)
    {
        ArgumentNullException.ThrowIfNull(postToUi);
        _store = store;
        _postToUi = postToUi;
        Status = store is null ? "Key store unavailable (no data directory). The timeline works without it." : string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public KeyPanelState State
    {
        get => _state;
        private set
        {
            if (SetField(ref _state, value))
            {
                Raise(nameof(ShowGenerate));
                Raise(nameof(ShowUnlock));
                Raise(nameof(ShowUnlocked));
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    /// <summary>Hex pubkey of the unlocked key. npub rendering arrives once Bech32 lives in Core.</summary>
    public string? PubkeyHex
    {
        get => _pubkeyHex;
        private set => SetField(ref _pubkeyHex, value);
    }

    /// <summary>The last export result (NIP-49 ciphertext). Shown read-only; copied only on an explicit Copy.</summary>
    public string? ExportedKey
    {
        get => _exportedKey;
        private set
        {
            if (SetField(ref _exportedKey, value))
            {
                Raise(nameof(HasExport));
            }
        }
    }

    public bool HasExport => _exportedKey is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool ShowGenerate => State == KeyPanelState.NoKey;

    public bool ShowUnlock => State == KeyPanelState.Locked;

    public bool ShowUnlocked => State == KeyPanelState.Unlocked;

    /// <summary>Reads the store's state. Asks nothing that needs a passphrase.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            Post(() => State = KeyPanelState.Unavailable);
            return;
        }

        try
        {
            SignerDescriptor? active = null;
            try
            {
                active = await _store.GetActiveSignerAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SignerUnavailableException)
            {
                // Not unlocked this session; fall through to the stored/none distinction.
            }

            if (active is not null)
            {
                Post(() => SetUnlocked(active));
                return;
            }

            var stored = await _store.HasStoredKeyAsync(cancellationToken).ConfigureAwait(false);
            Post(() =>
            {
                PubkeyHex = null;
                State = stored ? KeyPanelState.Locked : KeyPanelState.NoKey;
                Status = stored ? "A key is stored. Enter its passphrase to unlock." : "No key yet. Generate one to get a Nostr identity.";
            });
        }
        catch (Exception ex)
        {
            Post(() => Status = $"Could not read the key store ({ex.GetType().Name}).");
        }
    }

    public Task GenerateAsync(ReadOnlyMemory<char> passphrase, ReadOnlyMemory<char> confirmation, CancellationToken cancellationToken = default)
    {
        if (passphrase.Length == 0)
        {
            Status = "Enter a passphrase first.";
            return Task.CompletedTask;
        }

        if (!passphrase.Span.SequenceEqual(confirmation.Span))
        {
            Status = "The two passphrases do not match.";
            return Task.CompletedTask;
        }

        return RunAsync("Generating…", async store =>
        {
            var signer = await store.GenerateLocalKeyAsync(passphrase, cancellationToken).ConfigureAwait(false);
            Post(() =>
            {
                SetUnlocked(signer);
                Status = "Key generated and stored encrypted. Keep the passphrase: there is no recovery without it.";
            });
        });
    }

    public Task UnlockAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        if (passphrase.Length == 0)
        {
            Status = "Enter the passphrase first.";
            return Task.CompletedTask;
        }

        return RunAsync("Unlocking…", async store =>
        {
            var signer = await store.UnlockStoredKeyAsync(passphrase, cancellationToken).ConfigureAwait(false);
            Post(() =>
            {
                SetUnlocked(signer);
                Status = "Unlocked.";
            });
        });
    }

    public Task ExportAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        if (passphrase.Length == 0)
        {
            Status = "Enter a passphrase to encrypt the export with.";
            return Task.CompletedTask;
        }

        return RunAsync("Exporting…", async store =>
        {
            var ncryptsec = await store.ExportLocalKeyAsync(passphrase, cancellationToken).ConfigureAwait(false);
            Post(() =>
            {
                ExportedKey = ncryptsec;
                Status = "Export ready below. " + ExportClipboardWarning;
            });
        });
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        RunAsync("Signing out…", async store =>
        {
            await store.SignOutAsync(cancellationToken).ConfigureAwait(false);
            Post(() =>
            {
                PubkeyHex = null;
                ExportedKey = null;
            });
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            Post(() => Status = "Signed out. The stored key was removed; import or generate to continue.");
        });

    public void ClearExport() => ExportedKey = null;

    private async Task RunAsync(string busyStatus, Func<IKeyStore, Task> action)
    {
        if (_store is null)
        {
            Status = "Key store unavailable.";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = busyStatus;
        try
        {
            await action(_store).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Type only: messages from the key layer could echo input; the status line is not a log.
            Post(() => Status = $"Failed ({ex.GetType().Name}).");
        }
        finally
        {
            Post(() => IsBusy = false);
        }
    }

    private void SetUnlocked(SignerDescriptor signer)
    {
        PubkeyHex = signer.Pubkey;
        State = KeyPanelState.Unlocked;
    }

    private void Post(Action action) => _postToUi(action);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
