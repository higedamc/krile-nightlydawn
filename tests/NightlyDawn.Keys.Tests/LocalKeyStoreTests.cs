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

        var expectedId = EventCanonicalization.ComputeId(signed.Pubkey, signed.CreatedAt, signed.Kind, signed.Tags, signed.Content);
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
