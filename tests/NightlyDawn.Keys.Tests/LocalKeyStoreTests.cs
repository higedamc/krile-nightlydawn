using NBitcoin.Secp256k1;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.Keys.Tests;

public class LocalKeyStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public LocalKeyStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("nightlydawn-localkeystore-tests").FullName;
        _filePath = Path.Combine(_directory, "key.ncryptsec");
    }

    [Fact]
    public async Task GetActiveSignerAsync_ThrowsSignerUnavailable_BeforeAnyKeyExists()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.GetActiveSignerAsync());
    }

    [Fact]
    public async Task GenerateLocalKeyAsync_ActivatesASignerAndPersistsAnNcryptsecFile()
    {
        var fileStore = new FileKeyFileStore(_filePath);
        var store = new LocalKeyStore(fileStore);

        var descriptor = await store.GenerateLocalKeyAsync("a-passphrase".AsMemory());

        Assert.Equal(SignerKind.Nip49Local, descriptor.Kind);
        Assert.Equal(64, descriptor.Pubkey.Length); // 32-byte x-only pubkey, hex

        var persisted = await fileStore.ReadAsync();
        Assert.NotNull(persisted);
        Assert.StartsWith("ncryptsec1", persisted);
    }

    [Fact]
    public async Task GenerateLocalKeyAsync_ProducesDifferentKeysEachTime()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        var first = await store.GenerateLocalKeyAsync("pw".AsMemory());
        var second = await store.GenerateLocalKeyAsync("pw".AsMemory());

        Assert.NotEqual(first.Pubkey, second.Pubkey);
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsToTheSamePubkey()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));
        var generated = await store.GenerateLocalKeyAsync("generate-pass".AsMemory());

        var exported = await store.ExportLocalKeyAsync("export-pass".AsMemory());

        await store.SignOutAsync();
        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.GetActiveSignerAsync());

        var imported = await store.ImportLocalKeyAsync(exported, "export-pass".AsMemory());

        Assert.Equal(generated.Pubkey, imported.Pubkey);
    }

    [Fact]
    public async Task ImportRawKeyAsync_RoundTrips_ToTheSameNsecsPubkey()
    {
        var (nsec, expectedPubkeyHex) = MakeNsec();
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        var imported = await store.ImportRawKeyAsync(nsec.AsMemory(), "a-new-passphrase".AsMemory());

        Assert.Equal(expectedPubkeyHex, imported.Pubkey);
        Assert.Equal(SignerKind.Nip49Local, imported.Kind);
    }

    [Fact]
    public async Task ImportRawKeyAsync_PersistsAnNcryptsecThatUnlocksWithTheNewPassphrase()
    {
        var (nsec, expectedPubkeyHex) = MakeNsec();
        var fileStore = new FileKeyFileStore(_filePath);
        var store = new LocalKeyStore(fileStore);

        await store.ImportRawKeyAsync(nsec.AsMemory(), "a-new-passphrase".AsMemory());
        await store.LockAsync();

        var unlocked = await store.UnlockStoredKeyAsync("a-new-passphrase".AsMemory());

        Assert.Equal(expectedPubkeyHex, unlocked.Pubkey);
    }

    [Fact]
    public async Task ImportRawKeyAsync_RecordsKnownInsecureHandling_UnlikeAFreshlyGeneratedKey()
    {
        var (nsec, _) = MakeNsec();
        var fileStore = new FileKeyFileStore(_filePath);
        var store = new LocalKeyStore(fileStore);

        await store.ImportRawKeyAsync(nsec.AsMemory(), "pw".AsMemory());

        var persisted = await fileStore.ReadAsync();
        Assert.True(Bech32.TryDecode("ncryptsec", persisted!, out var payload));
        Assert.Equal(KeySecurity.KnownInsecureHandling, Nip49KeyEncryption.ReadKeySecurity(payload));
    }

    [Fact]
    public async Task ImportRawKeyAsync_RejectsAMalformedNsec()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<FormatException>(() => store.ImportRawKeyAsync("not-an-nsec".AsMemory(), "pw".AsMemory()));
    }

    [Fact]
    public async Task ImportRawKeyAsync_RejectsAWrongLengthPayload()
    {
        var shortPayload = Bech32.Encode("nsec", new byte[16]);
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<FormatException>(() => store.ImportRawKeyAsync(shortPayload.AsMemory(), "pw".AsMemory()));
    }

    /// <summary>All-zero is 32 bytes but not a valid secp256k1 scalar. Without the TryCreate check this
    /// surfaces as NBitcoin's ArgumentException ("Invalid ec private key"), which is not one of the
    /// exceptions <see cref="NightlyDawn.Core.IKeyStore.ImportRawKeyAsync"/> documents (§63).</summary>
    [Fact]
    public async Task ImportRawKeyAsync_RejectsAnOutOfRangeScalar()
    {
        var allZero = Bech32.Encode("nsec", new byte[32]);
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<FormatException>(() => store.ImportRawKeyAsync(allZero.AsMemory(), "pw".AsMemory()));
    }

    /// <summary>Builds a valid <c>nsec1...</c> for a freshly generated secp256k1 key the same way
    /// <see cref="LocalKeyStore.GenerateLocalKeyAsync"/> does, plus the hex x-only pubkey it decodes to —
    /// there is no store API that returns a raw key, so tests needing one as input build it directly, exactly
    /// as a NIP-49-unaware tool producing a bare nsec would.</summary>
    private static (string Nsec, string PubkeyHex) MakeNsec()
    {
        var rawKey = new byte[32];
        ECPrivKey? privKey = null;
        while (privKey is null)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(rawKey);
            ECPrivKey.TryCreate(rawKey, out privKey);
        }

        var pubkeyHex = Convert.ToHexString(privKey.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();
        privKey.Dispose();
        return (Bech32.Encode("nsec", rawKey), pubkeyHex);
    }

    [Fact]
    public async Task ExportLocalKeyAsync_ThrowsSignerUnavailable_WhenNoKeyIsActive()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.ExportLocalKeyAsync("pw".AsMemory()));
    }

    [Fact]
    public async Task SignEventAsync_ProducesASelfConsistentSignature()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));
        var descriptor = await store.GenerateLocalKeyAsync("pw".AsMemory());

        var unsigned = new UnsignedNostrEvent(
            Pubkey: null,
            CreatedAt: 1700000000,
            Kind: 1,
            Tags: [],
            Content: "hello from NightlyDawn");

        var signed = await store.SignEventAsync(unsigned);

        Assert.Equal(descriptor.Pubkey, signed.Pubkey);
        Assert.Equal(64, signed.Id.Length);
        Assert.Equal(128, signed.Sig.Length);

        var expectedId = NostrEventCanonicalization.ComputeId(signed.Pubkey, signed.CreatedAt, signed.Kind, signed.Tags, signed.Content);
        Assert.Equal(Convert.ToHexString(expectedId).ToLowerInvariant(), signed.Id);
    }

    [Fact]
    public async Task SignEventAsync_ThrowsSignerUnavailable_WhenNoKeyIsActive()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));
        var unsigned = new UnsignedNostrEvent(null, 1700000000, 1, [], "x");

        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.SignEventAsync(unsigned));
    }

    [Fact]
    public async Task SignOutAsync_ClearsTheActiveSignerAndDeletesThePersistedFile()
    {
        var fileStore = new FileKeyFileStore(_filePath);
        var store = new LocalKeyStore(fileStore);
        await store.GenerateLocalKeyAsync("pw".AsMemory());

        await store.SignOutAsync();

        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.GetActiveSignerAsync());
        Assert.Null(await fileStore.ReadAsync());
    }

    [Fact]
    public async Task BeginRemoteSignerPairingAsync_ThrowsNotSupported()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<NotSupportedException>(() => store.BeginRemoteSignerPairingAsync());
    }

    [Fact]
    public async Task ConnectBunkerAsync_ThrowsNotSupported()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<NotSupportedException>(() => store.ConnectBunkerAsync("bunker://example"));
    }

    [Fact]
    public async Task Nip44EncryptAsync_ThrowsNotSupported()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<NotSupportedException>(() => store.Nip44EncryptAsync("peer", "plaintext"));
    }

    [Fact]
    public async Task Nip44DecryptAsync_ThrowsNotSupported()
    {
        var store = new LocalKeyStore(new FileKeyFileStore(_filePath));

        await Assert.ThrowsAsync<NotSupportedException>(() => store.Nip44DecryptAsync("peer", "ciphertext"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
