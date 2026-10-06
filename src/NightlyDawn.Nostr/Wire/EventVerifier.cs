using NBitcoin.Secp256k1;
using NightlyDawn.Core;
using Sha256 = System.Security.Cryptography.SHA256;

namespace NightlyDawn.Nostr.Wire;

internal enum VerificationResult
{
    Valid,
    MalformedFields,
    IdMismatch,
    InvalidSignature,
}

/// <summary>
/// Relays are hostile input. Every received event is checked here before anything else sees it:
/// the id must equal SHA-256 of the canonical serialization, and the signature must be a valid
/// BIP-340 Schnorr signature of that id under the event's x-only pubkey.
/// </summary>
internal static class EventVerifier
{
    public static VerificationResult Verify(NostrEvent e)
    {
        if (!IsLowerHex(e.Id, 64) || !IsLowerHex(e.Pubkey, 64) || !IsLowerHex(e.Sig, 128) || e.Kind < 0)
        {
            return VerificationResult.MalformedFields;
        }

        var canonical = NostrJson.CanonicalEventBytes(e.Pubkey, e.CreatedAt, e.Kind, e.Tags, e.Content);
        Span<byte> digest = stackalloc byte[32];
        Sha256.HashData(canonical, digest);
        var idBytes = Convert.FromHexString(e.Id);
        if (!digest.SequenceEqual(idBytes))
        {
            return VerificationResult.IdMismatch;
        }

        if (!ECXOnlyPubKey.TryCreate(Convert.FromHexString(e.Pubkey), out var pubkey) ||
            !SecpSchnorrSignature.TryCreate(Convert.FromHexString(e.Sig), out var signature) ||
            !pubkey.SigVerifyBIP340(signature, idBytes))
        {
            return VerificationResult.InvalidSignature;
        }

        return VerificationResult.Valid;
    }

    public static string ComputeId(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content)
    {
        var canonical = NostrJson.CanonicalEventBytes(pubkey, createdAt, kind, tags, content);
        return Convert.ToHexString(Sha256.HashData(canonical)).ToLowerInvariant();
    }

    internal static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
