using NBitcoin.Secp256k1;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Wire;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>Test-only signer with a throwaway in-memory key. Production code never touches raw keys (that is IKeyStore's job in 1b).</summary>
internal sealed class TestSigner
{
    private readonly ECPrivKey _key;

    public TestSigner(byte seed = 1)
    {
        var bytes = new byte[32];
        bytes[31] = seed;
        _key = ECPrivKey.Create(bytes);
        PubkeyHex = Convert.ToHexString(_key.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();
    }

    public string PubkeyHex { get; }

    public NostrEvent Sign(int kind, string content, IReadOnlyList<IReadOnlyList<string>>? tags = null, long createdAt = 1_700_000_000)
    {
        tags ??= [];
        var id = EventVerifier.ComputeId(PubkeyHex, createdAt, kind, tags, content);
        var sig = _key.SignBIP340(Convert.FromHexString(id));
        return new NostrEvent(id, PubkeyHex, createdAt, kind, tags, content, Convert.ToHexString(sig.ToBytes()).ToLowerInvariant());
    }
}
