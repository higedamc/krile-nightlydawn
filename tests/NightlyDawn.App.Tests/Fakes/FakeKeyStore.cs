using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>
/// In-process <see cref="IKeyStore"/> for <c>TimelineViewModelTests</c> (L5j, plan §18.2): the column
/// only ever calls <see cref="GetActiveSignerAsync"/>, so every other member throws.
///
/// <para><see cref="ActiveSignerDelay"/> is a one-shot: when set, the <em>next</em> call to
/// <see cref="GetActiveSignerAsync"/> returns that pending task (and clears the field so the call after it
/// falls through to <see cref="ActiveSigner"/> instead) -- this is what lets a test hold one account lookup
/// in flight while a second, later call resolves immediately, the race <c>TimelineViewModel.Subscribe</c>'s
/// generation guard has to survive.</para>
/// </summary>
internal sealed class FakeKeyStore : IKeyStore
{
    public SignerDescriptor? ActiveSigner { get; set; }

    public TaskCompletionSource<SignerDescriptor>? ActiveSignerDelay { get; set; }

    public Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default)
    {
        if (ActiveSignerDelay is { } delay)
        {
            ActiveSignerDelay = null;
            return delay.Task;
        }

        return ActiveSigner is { } signer
            ? Task.FromResult(signer)
            : Task.FromException<SignerDescriptor>(new SignerUnavailableException("No local key is active. Generate or import one first."));
    }

    public Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<string> Nip44EncryptAsync(string peerPubkey, string plaintext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<string> Nip44DecryptAsync(string peerPubkey, string ciphertext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<RemoteSignerPairing> BeginRemoteSignerPairingAsync(IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<SignerDescriptor> ConnectBunkerAsync(string bunkerUri, IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<SignerDescriptor> ImportRawKeyAsync(ReadOnlyMemory<char> nsec, ReadOnlyMemory<char> newPassphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<SignerDescriptor> GenerateLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<string> ExportLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task LockAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<bool> HasStoredKeyAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");

    public Task<SignerDescriptor> UnlockStoredKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TimelineViewModel never calls this.");
}
