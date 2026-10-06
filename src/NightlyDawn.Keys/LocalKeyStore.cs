using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using NightlyDawn.Core;

namespace NightlyDawn.Keys;

/// <summary>
/// <see cref="IKeyStore"/>'s local-key half (NIP-46 remote signing is out of scope for
/// this leaf and throws <see cref="NotSupportedException"/>, per Lead/???: Amber/bunker
/// support lands separately). Backed by a NIP-49-encrypted <c>ncryptsec1...</c> blob via
/// <see cref="IKeyFileStore"/>; the raw secret key lives only in <see cref="_privateKey"/>
/// (zeroed on sign-out) and is never returned to callers — <see cref="ExportLocalKeyAsync"/>
/// is the only way a key leaves this store, and only as NIP-49 ciphertext.
///
/// There is no implicit "load the persisted key at startup": a prior run's
/// <c>ncryptsec1...</c> sits on disk, but decrypting it needs a passphrase this class has
/// no way to ask for. A caller that finds a persisted file must call
/// <see cref="ImportLocalKeyAsync"/> with a user-supplied passphrase to re-establish an
/// active signer for the session.
/// </summary>
public sealed class LocalKeyStore(IKeyFileStore fileStore) : IKeyStore
{
    /// <summary>64 MiB / ~100ms per the NIP-49 spec table — also the value the spec's own test vector uses.</summary>
    public const byte DefaultLogN = 16;

    private byte[]? _privateKey;
    private string? _pubkeyHex;
    private KeySecurity _keySecurity;

    public Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireActiveSigner());

    public Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default)
    {
        RequireActiveSigner();

        var pubkey = _pubkeyHex!;
        var id = NostrEventCanonicalization.ComputeId(pubkey, unsignedEvent.CreatedAt, unsignedEvent.Kind, unsignedEvent.Tags, unsignedEvent.Content);

        using var privKey = ECPrivKey.Create(_privateKey);
        var signature = privKey.SignBIP340(id);

        var nostrEvent = new NostrEvent(
            Convert.ToHexString(id).ToLowerInvariant(),
            pubkey,
            unsignedEvent.CreatedAt,
            unsignedEvent.Kind,
            unsignedEvent.Tags,
            unsignedEvent.Content,
            Convert.ToHexString(signature.ToBytes()).ToLowerInvariant());

        return Task.FromResult(nostrEvent);
    }

    public Task<string> Nip44EncryptAsync(string peerPubkey, string plaintext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NIP-44 is out of scope for leaf 1b; DMs are not yet implemented.");

    public Task<string> Nip44DecryptAsync(string peerPubkey, string ciphertext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NIP-44 is out of scope for leaf 1b; DMs are not yet implemented.");

    public Task<RemoteSignerPairing> BeginRemoteSignerPairingAsync(IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NIP-46 remote signers are out of scope for leaf 1b (??? deferred Amber/bunker support).");

    public Task<SignerDescriptor> ConnectBunkerAsync(string bunkerUri, IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NIP-46 remote signers are out of scope for leaf 1b (??? deferred Amber/bunker support).");

    public async Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        var payload = Bech32.Decode("ncryptsec", nip49EncryptedKey);
        var rawKey = Nip49KeyEncryption.Decrypt(payload, passphrase);

        // Preserve the imported key's own recorded security history rather than
        // overwriting it — an imported key does not become "freshly generated".
        var keySecurity = Nip49KeyEncryption.ReadKeySecurity(payload);

        var descriptor = SetActiveKey(rawKey, keySecurity);
        await fileStore.WriteAsync(nip49EncryptedKey, cancellationToken).ConfigureAwait(false);
        return descriptor;
    }

    public async Task<SignerDescriptor> GenerateLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        var rawKey = new byte[32];
        ECPrivKey? privKey = null;
        try
        {
            while (privKey is null)
            {
                RandomNumberGenerator.Fill(rawKey);
                ECPrivKey.TryCreate(rawKey, out privKey);
            }

            // Generated in-process and never handled as plaintext outside this store.
            var descriptor = SetActiveKey(rawKey, KeySecurity.NotKnownInsecureHandling);

            var ncryptsec = EncryptActiveKey(passphrase, DefaultLogN);
            await fileStore.WriteAsync(ncryptsec, cancellationToken).ConfigureAwait(false);

            return descriptor;
        }
        finally
        {
            privKey?.Dispose();
            CryptographicOperations.ZeroMemory(rawKey);
        }
    }

    /// <summary>Always re-encrypts at <see cref="DefaultLogN"/>, not the log_n the active key happened to be generated or imported under — exporting re-applies this store's current cost policy rather than preserving whatever a possibly-older or foreign ncryptsec used. Deliberate (a backup should get this build's parameters), but worth knowing: the log_n of the string you get back is not necessarily the log_n of the one you imported.</summary>
    public Task<string> ExportLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        RequireActiveSigner();

        return Task.FromResult(EncryptActiveKey(passphrase, DefaultLogN));
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_privateKey is not null)
        {
            CryptographicOperations.ZeroMemory(_privateKey);
        }

        _privateKey = null;
        _pubkeyHex = null;
        _keySecurity = default;

        return fileStore.DeleteAsync(cancellationToken);
    }

    public async Task<bool> HasStoredKeyAsync(CancellationToken cancellationToken = default) =>
        await fileStore.ReadAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Decrypts the persisted <c>ncryptsec1</c> for this session. Deliberately does not call <see cref="IKeyFileStore.WriteAsync"/>: the stored bytes are already what we want on disk, and rewriting on every unlock would turn a read-only action into a write to the only copy.</summary>
    public async Task<SignerDescriptor> UnlockStoredKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default)
    {
        var stored = await fileStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new SignerUnavailableException("No local key is stored. Generate or import one first.");

        var payload = Bech32.Decode("ncryptsec", stored);
        var rawKey = Nip49KeyEncryption.Decrypt(payload, passphrase);
        return SetActiveKey(rawKey, Nip49KeyEncryption.ReadKeySecurity(payload));
    }

    private SignerDescriptor RequireActiveSigner()
    {
        if (_privateKey is null || _pubkeyHex is null)
        {
            throw new SignerUnavailableException("No local key is active. Generate or import one first.");
        }

        return new SignerDescriptor(_pubkeyHex, SignerKind.Nip49Local);
    }

    private SignerDescriptor SetActiveKey(byte[] rawKey, KeySecurity keySecurity)
    {
        using var privKey = ECPrivKey.Create(rawKey);
        var pubkeyHex = Convert.ToHexString(privKey.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();

        _privateKey = (byte[])rawKey.Clone();
        _pubkeyHex = pubkeyHex;
        _keySecurity = keySecurity;

        return new SignerDescriptor(pubkeyHex, SignerKind.Nip49Local);
    }

    private string EncryptActiveKey(ReadOnlyMemory<char> passphrase, byte logN)
    {
        var payload = Nip49KeyEncryption.Encrypt(_privateKey!, passphrase, logN, _keySecurity);
        return Bech32.Encode("ncryptsec", payload);
    }
}
