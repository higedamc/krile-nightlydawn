using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>
/// In-process <see cref="IKeyStore"/> for publisher tests (L3a): signs with a real throwaway key via
/// <see cref="TestSigner"/> (so a mapped/signed <see cref="NostrEvent"/> looks structurally real) but skips
/// everything else -- pairing, import/export, lock/unlock -- which <see cref="NotePublisher"/> never calls.
///
/// <para><see cref="ActiveSigner"/> is mutable so a test can swap the active signer mid-scenario and prove
/// <see cref="NotePublisher"/> reads it fresh on every call rather than caching the first one (plan §9.2-3).</para>
/// </summary>
internal sealed class FakeKeyStore : IKeyStore
{
    public TestSigner ActiveSigner { get; set; } = new(seed: 1);

    public int GetActiveSignerCallCount { get; private set; }

    public Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default)
    {
        GetActiveSignerCallCount++;
        return Task.FromResult(new SignerDescriptor(ActiveSigner.PubkeyHex, SignerKind.Nip49Local));
    }

    public Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default) =>
        Task.FromResult(ActiveSigner.Sign(unsignedEvent.Kind, unsignedEvent.Content, unsignedEvent.Tags, unsignedEvent.CreatedAt));

    public Task<string> Nip44EncryptAsync(string peerPubkey, string plaintext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<string> Nip44DecryptAsync(string peerPubkey, string ciphertext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<RemoteSignerPairing> BeginRemoteSignerPairingAsync(IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<SignerDescriptor> ConnectBunkerAsync(string bunkerUri, IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<SignerDescriptor> GenerateLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<string> ExportLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task LockAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<bool> HasStoredKeyAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<SignerDescriptor> UnlockStoredKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");
}
