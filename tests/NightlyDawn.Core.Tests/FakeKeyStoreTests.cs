using System.Threading;
using Xunit;

namespace NightlyDawn.Core.Tests;

/// <summary>Proves <see cref="IKeyStore"/> is implementable as written and that the generate/export shape (B11) round-trips without ever exposing a raw secret key through the interface.</summary>
public class FakeKeyStoreTests
{
    [Fact]
    public async Task GenerateThenExport_RoundTripsThroughNip49Ciphertext_WithNoRawKeyExposed()
    {
        var store = new FakeLocalKeyStore();

        var descriptor = await store.GenerateLocalKeyAsync("correct-horse-battery-staple".AsMemory());

        Assert.Equal(SignerKind.Nip49Local, descriptor.Kind);
        Assert.NotEmpty(descriptor.Pubkey);

        var exported = await store.ExportLocalKeyAsync("correct-horse-battery-staple".AsMemory());

        Assert.StartsWith("ncryptsec1", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HasStoredKey_IsFalseBeforeGenerate_TrueAfter_AndNeverNeedsAPassphrase()
    {
        var store = new FakeLocalKeyStore();

        Assert.False(await store.HasStoredKeyAsync());
        await store.GenerateLocalKeyAsync("pw".AsMemory());
        Assert.True(await store.HasStoredKeyAsync());
    }

    private sealed class FakeLocalKeyStore : IKeyStore
    {
        private string? _ncryptsec;
        private string? _pubkey;

        public Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SignerDescriptor(_pubkey ?? throw new SignerUnavailableException("no key"), SignerKind.Nip49Local));

        public Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> Nip44EncryptAsync(string peerPubkey, string plaintext, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> Nip44DecryptAsync(string peerPubkey, string ciphertext, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RemoteSignerPairing> BeginRemoteSignerPairingAsync(IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<SignerDescriptor> ConnectBunkerAsync(string bunkerUri, IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
        {
            _ncryptsec = nip49EncryptedKey;
            _pubkey = "fake-imported-pubkey";
            return Task.FromResult(new SignerDescriptor(_pubkey, SignerKind.Nip49Local));
        }

        public Task<SignerDescriptor> GenerateLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
        {
            // A real implementation draws from a CSPRNG and checks the secp256k1 valid range;
            // this fake only needs to prove the interface shape round-trips.
            _pubkey = "fake-generated-pubkey";
            _ncryptsec = "ncryptsec1fakefakefake";
            return Task.FromResult(new SignerDescriptor(_pubkey, SignerKind.Nip49Local));
        }

        public Task<string> ExportLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
            Task.FromResult(_ncryptsec ?? throw new SignerUnavailableException("no local key"));

        public Task<bool> HasStoredKeyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_ncryptsec is not null);

        public Task<SignerDescriptor> UnlockStoredKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SignerDescriptor(_pubkey ?? throw new SignerUnavailableException("nothing stored"), SignerKind.Nip49Local));

        public Task LockAsync(CancellationToken cancellationToken = default)
        {
            _pubkey = null; // the fake has no in-memory key material; _ncryptsec (persistence) is deliberately kept
            return Task.CompletedTask;
        }

        public Task SignOutAsync(CancellationToken cancellationToken = default)
        {
            _ncryptsec = null;
            _pubkey = null;
            return Task.CompletedTask;
        }
    }
}
