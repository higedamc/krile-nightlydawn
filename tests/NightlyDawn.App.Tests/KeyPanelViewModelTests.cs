using System.Security.Cryptography;
using NightlyDawn.App.Keys;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

public class KeyPanelViewModelTests
{
    private static KeyPanelViewModel New(IKeyStore? store) => new(store, postToUi: a => a());

    [Fact]
    public async Task NoStore_IsUnavailable_AndEveryActionIsRefused()
    {
        var vm = New(null);
        await vm.RefreshAsync();

        Assert.Equal(KeyPanelState.Unavailable, vm.State);
        Assert.Contains("unavailable", vm.Status, StringComparison.OrdinalIgnoreCase);
        await vm.GenerateAsync("pw".AsMemory(), "pw".AsMemory());
        Assert.Equal(KeyPanelState.Unavailable, vm.State);
    }

    [Fact]
    public async Task Refresh_DistinguishesNoKey_Locked_AndUnlocked_WithoutAPassphrase()
    {
        var store = new FakeKeyStore();
        var vm = New(store);

        await vm.RefreshAsync();
        Assert.Equal(KeyPanelState.NoKey, vm.State);
        Assert.True(vm.ShowGenerate);

        store.Stored = "ncryptsec1stored";
        await vm.RefreshAsync();
        Assert.Equal(KeyPanelState.Locked, vm.State);
        Assert.True(vm.ShowUnlock);
        Assert.Null(vm.PubkeyHex);

        store.Active = new SignerDescriptor("ab".PadRight(64, '0'), SignerKind.Nip49Local);
        await vm.RefreshAsync();
        Assert.Equal(KeyPanelState.Unlocked, vm.State);
        Assert.Equal(store.Active.Pubkey, vm.PubkeyHex);
        Assert.Equal(0, store.PassphraseCalls); // refresh never asked for a passphrase
    }

    [Fact]
    public async Task Generate_RequiresMatchingNonEmptyPassphrases_ThenUnlocks()
    {
        var store = new FakeKeyStore();
        var vm = New(store);
        await vm.RefreshAsync();

        await vm.GenerateAsync("".AsMemory(), "".AsMemory());
        Assert.Equal(0, store.Generates);
        Assert.Equal(KeyPanelState.NoKey, vm.State);

        await vm.GenerateAsync("pw-one".AsMemory(), "pw-two".AsMemory());
        Assert.Equal(0, store.Generates);
        Assert.Contains("do not match", vm.Status);

        await vm.GenerateAsync("correct horse".AsMemory(), "correct horse".AsMemory());
        Assert.Equal(1, store.Generates);
        Assert.Equal(KeyPanelState.Unlocked, vm.State);
        Assert.Equal(store.Active!.Pubkey, vm.PubkeyHex);
        Assert.Contains("no recovery", vm.Status);
    }

    [Fact]
    public async Task Unlock_UsesTheStoredKeyPath_AndWrongPassphraseShowsTypeOnly()
    {
        var store = new FakeKeyStore { Stored = "ncryptsec1stored", UnlockThrows = new CryptographicException("MAC check failed: secret details") };
        var vm = New(store);
        await vm.RefreshAsync();
        Assert.Equal(KeyPanelState.Locked, vm.State);

        await vm.UnlockAsync("wrong".AsMemory());

        Assert.Equal(KeyPanelState.Locked, vm.State);
        Assert.Equal("Failed (CryptographicException).", vm.Status);
        Assert.DoesNotContain("secret", vm.Status);
        Assert.Equal(0, store.Imports); // unlock must not go through Import (which rewrites the file)

        store.UnlockThrows = null;
        await vm.UnlockAsync("right".AsMemory());
        Assert.Equal(KeyPanelState.Unlocked, vm.State);
        Assert.Equal(2, store.Unlocks); // the failed attempt and the successful one both went through UnlockStoredKeyAsync
    }

    [Fact]
    public async Task Export_ShowsCiphertextOnly_AndClearWorks()
    {
        var store = new FakeKeyStore { Stored = "x", Active = new SignerDescriptor("cd".PadRight(64, '0'), SignerKind.Nip49Local) };
        var vm = New(store);
        await vm.RefreshAsync();

        await vm.ExportAsync("".AsMemory());
        Assert.False(vm.HasExport);

        await vm.ExportAsync("backup-pw".AsMemory());
        Assert.True(vm.HasExport);
        Assert.StartsWith("ncryptsec1", vm.ExportedKey, StringComparison.Ordinal);
        Assert.Contains("Nothing is copied until you press Copy", vm.Status);

        vm.ClearExport();
        Assert.False(vm.HasExport);
        Assert.Null(vm.ExportedKey);
    }

    [Fact]
    public async Task SignOut_IsTwoStep_FirstPressOnlyArms_SecondDeletes()
    {
        var store = new FakeKeyStore { Stored = "x", Active = new SignerDescriptor("ef".PadRight(64, '0'), SignerKind.Nip49Local) };
        var vm = New(store);
        await vm.RefreshAsync();
        await vm.ExportAsync("pw".AsMemory());

        await vm.SignOutAsync();

        Assert.True(vm.SignOutArmed);
        Assert.Equal(0, store.SignOuts); // nothing destroyed yet
        Assert.Equal(KeyPanelState.Unlocked, vm.State);
        Assert.Contains("no undo", vm.Status);
        Assert.Contains("Lock", vm.Status);

        await vm.SignOutAsync();

        Assert.False(vm.SignOutArmed);
        Assert.Equal(1, store.SignOuts);
        Assert.Equal(KeyPanelState.NoKey, vm.State);
        Assert.Null(vm.PubkeyHex);
        Assert.Null(vm.ExportedKey);
    }

    [Fact]
    public async Task ArmedSignOut_IsDisarmedByCancel_Lock_Export_OrRefresh()
    {
        var store = new FakeKeyStore { Stored = "x", Active = new SignerDescriptor("ef".PadRight(64, '0'), SignerKind.Nip49Local) };
        var vm = New(store);
        await vm.RefreshAsync();

        await vm.SignOutAsync();
        vm.CancelSignOut();
        Assert.False(vm.SignOutArmed);
        Assert.Equal("Sign out cancelled.", vm.Status);

        await vm.SignOutAsync();
        await vm.ExportAsync("pw".AsMemory());
        Assert.False(vm.SignOutArmed);

        await vm.SignOutAsync();
        await vm.RefreshAsync();
        Assert.False(vm.SignOutArmed);

        await vm.SignOutAsync();
        await vm.LockAsync();
        Assert.False(vm.SignOutArmed);
        Assert.Equal(0, store.SignOuts); // four armings, zero deletions
    }

    [Fact]
    public async Task Lock_ReturnsToLocked_WithoutDeleting_AndUnlockWorksAgain()
    {
        var store = new FakeKeyStore { Stored = "x", Active = new SignerDescriptor("ef".PadRight(64, '0'), SignerKind.Nip49Local) };
        var vm = New(store);
        await vm.RefreshAsync();
        await vm.ExportAsync("pw".AsMemory());

        await vm.LockAsync();

        Assert.Equal(KeyPanelState.Locked, vm.State);
        Assert.Null(vm.PubkeyHex);
        Assert.Null(vm.ExportedKey);
        Assert.Equal(1, store.Locks);
        Assert.Equal(0, store.SignOuts);
        Assert.NotNull(store.Stored); // the file is still there

        await vm.UnlockAsync("pw".AsMemory());
        Assert.Equal(KeyPanelState.Unlocked, vm.State);
    }

    [Fact]
    public async Task StateChange_RaisesTheDerivedVisibilityProperties()
    {
        var store = new FakeKeyStore();
        var vm = New(store);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.RefreshAsync();

        Assert.Contains(nameof(KeyPanelViewModel.State), raised);
        Assert.Contains(nameof(KeyPanelViewModel.ShowGenerate), raised);
        Assert.Contains(nameof(KeyPanelViewModel.ShowUnlock), raised);
        Assert.Contains(nameof(KeyPanelViewModel.ShowUnlocked), raised);
    }

    /// <summary>Scriptable IKeyStore: no crypto, counts which contract methods the panel touched.</summary>
    private sealed class FakeKeyStore : IKeyStore
    {
        public string? Stored { get; set; }
        public SignerDescriptor? Active { get; set; }
        public Exception? UnlockThrows { get; set; }
        public int Generates { get; private set; }
        public int Unlocks { get; private set; }
        public int Imports { get; private set; }
        public int SignOuts { get; private set; }
        public int Locks { get; private set; }
        public int PassphraseCalls { get; private set; }

        public Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Active ?? throw new SignerUnavailableException("not unlocked"));

        public Task<bool> HasStoredKeyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Stored is not null);

        public Task<SignerDescriptor> UnlockStoredKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
        {
            PassphraseCalls++;
            Unlocks++;
            if (Stored is null)
            {
                throw new SignerUnavailableException("nothing stored");
            }

            if (UnlockThrows is not null)
            {
                throw UnlockThrows;
            }

            Active = new SignerDescriptor("12".PadRight(64, '0'), SignerKind.Nip49Local);
            return Task.FromResult(Active);
        }

        public Task<SignerDescriptor> GenerateLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
        {
            PassphraseCalls++;
            Generates++;
            Stored = "ncryptsec1generated";
            Active = new SignerDescriptor("34".PadRight(64, '0'), SignerKind.Nip49Local);
            return Task.FromResult(Active);
        }

        public Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
        {
            PassphraseCalls++;
            Imports++;
            Stored = nip49EncryptedKey;
            Active = new SignerDescriptor("56".PadRight(64, '0'), SignerKind.Nip49Local);
            return Task.FromResult(Active);
        }

        public Task<string> ExportLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
        {
            PassphraseCalls++;
            _ = Active ?? throw new SignerUnavailableException("not unlocked");
            return Task.FromResult("ncryptsec1exported");
        }

        public Task LockAsync(CancellationToken cancellationToken = default)
        {
            Locks++;
            Active = null; // Stored is kept: lock is memory-only
            return Task.CompletedTask;
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            SignOuts++;
            Stored = null;
            Active = null;
            return Task.CompletedTask;
        }

        public Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> Nip44EncryptAsync(string peerPubkey, string plaintext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> Nip44DecryptAsync(string peerPubkey, string ciphertext, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<RemoteSignerPairing> BeginRemoteSignerPairingAsync(IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SignerDescriptor> ConnectBunkerAsync(string bunkerUri, IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
