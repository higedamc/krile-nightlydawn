using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace NightlyDawn.Keys;

/// <summary>NIP-49 key security byte (<c>KEY_SECURITY_BYTE</c>).</summary>
public enum KeySecurity : byte
{
    KnownInsecureHandling = 0x00,
    NotKnownInsecureHandling = 0x01,
    Unknown = 0x02,
}

/// <summary>
/// NIP-49 (https://github.com/nostr-protocol/nips/blob/master/49.md) private key
/// encryption: scrypt key derivation (BouncyCastle.Cryptography) + XChaCha20-Poly1305
/// (NSec.Cryptography/libsodium) — no hand-rolled cryptographic primitives. Operates on
/// the raw 91-byte payload; bech32 (<c>ncryptsec1...</c>) wrapping is <see cref="Bech32"/>'s
/// job.
///
/// Escape route, if native binary bundling for an A2159/AppImage build ever becomes a
/// problem: BouncyCastle.Cryptography 2.7.0 also ships <c>Org.BouncyCastle.Crypto.Modes.XChaCha20Poly1305</c>
/// (pure managed, no native dependency), so this module could drop NSec/libsodium entirely
/// and use BouncyCastle for both halves. Not done now — libsodium's audited C AEAD stays
/// preferred for encrypting the user's key — but if it's ever needed, re-running this
/// class's official-NIP-49-vector test against the BouncyCastle AEAD path is the only
/// re-verification required before switching.
/// </summary>
internal static class Nip49KeyEncryption
{
    private const byte Version = 0x02;
    private const int SaltSize = 16;
    private const int NonceSize = 24;
    private const int KeySize = 32;
    private const int KeySecurityByteOffset = 2 + SaltSize + NonceSize;

    /// <summary>Spec's own table tops out at log_n=22 (16 GiB, r=8). Below 1, N=1 degenerates scrypt to a single unsalted-in-effect hash iteration.</summary>
    private const byte MinLogN = 1;
    private const byte MaxLogN = 22;

    /// <summary>1 (version) + 1 (log_n) + 16 (salt) + 24 (nonce) + 1 (key security) + 48 (32-byte key + 16-byte Poly1305 tag) = 91, per the spec.</summary>
    public const int PayloadSize = 91;

    public static byte[] Encrypt(ReadOnlySpan<byte> privateKey, ReadOnlyMemory<char> passphrase, byte logN, KeySecurity keySecurity)
    {
        if (privateKey.Length != KeySize)
        {
            throw new ArgumentException($"Private key must be {KeySize} bytes.", nameof(privateKey));
        }

        // Defensive, not a security boundary (logN is internal-caller-supplied here, unlike Decrypt's) — catches a bad constant before it ever reaches disk.
        if (logN is < MinLogN or > MaxLogN)
        {
            throw new ArgumentOutOfRangeException(nameof(logN), logN, $"log_n must be in {MinLogN}..{MaxLogN}.");
        }

        Span<byte> salt = stackalloc byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        Span<byte> nonce = stackalloc byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var symmetricKeyBytes = DeriveScryptKey(passphrase, salt, logN);
        try
        {
            using var key = Key.Import(AeadAlgorithm.XChaCha20Poly1305, symmetricKeyBytes, KeyBlobFormat.RawSymmetricKey);

            Span<byte> associatedData = [(byte)keySecurity];
            var ciphertext = AeadAlgorithm.XChaCha20Poly1305.Encrypt(key, nonce, associatedData, privateKey);

            var payload = new byte[PayloadSize];
            var offset = 0;
            payload[offset++] = Version;
            payload[offset++] = logN;
            salt.CopyTo(payload.AsSpan(offset, SaltSize));
            offset += SaltSize;
            nonce.CopyTo(payload.AsSpan(offset, NonceSize));
            offset += NonceSize;
            payload[offset++] = (byte)keySecurity;
            ciphertext.CopyTo(payload.AsSpan(offset));

            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(symmetricKeyBytes);
        }
    }

    /// <summary>Reads the <c>KEY_SECURITY_BYTE</c> out of a payload without decrypting it — the offset lives here, as the single source of truth for the payload layout, rather than being recomputed by callers (e.g. to decide whether to overwrite a key's recorded security history on import).</summary>
    /// <exception cref="FormatException">The payload's version byte is unsupported, or the value isn't one of the three defined <see cref="KeySecurity"/> cases.</exception>
    public static KeySecurity ReadKeySecurity(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadSize)
        {
            throw new ArgumentException($"NIP-49 payload must be {PayloadSize} bytes.", nameof(payload));
        }

        if (payload[0] != Version)
        {
            throw new FormatException($"Unsupported ncryptsec version byte: 0x{payload[0]:x2}.");
        }

        var value = payload[KeySecurityByteOffset];
        if (!Enum.IsDefined(typeof(KeySecurity), value))
        {
            throw new FormatException($"Unrecognized NIP-49 key security byte: 0x{value:x2}.");
        }

        return (KeySecurity)value;
    }

    /// <exception cref="FormatException">The payload's version byte is unsupported, or log_n is outside 1..22 (an ncryptsec string is pasted-in, untrusted input: an attacker-chosen log_n above the spec's own table either exhausts memory computing N = 2^log_n, or — at 31/32 — hits C#'s 5-bit shift-count masking and produces a negative or wrapped-around N).</exception>
    /// <exception cref="CryptographicException">The passphrase is wrong or the ciphertext was tampered with.</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> payload, ReadOnlyMemory<char> passphrase)
    {
        if (payload.Length != PayloadSize)
        {
            throw new ArgumentException($"NIP-49 payload must be {PayloadSize} bytes.", nameof(payload));
        }

        if (payload[0] != Version)
        {
            throw new FormatException($"Unsupported ncryptsec version byte: 0x{payload[0]:x2}.");
        }

        var logN = payload[1];
        if (logN is < MinLogN or > MaxLogN)
        {
            throw new FormatException($"ncryptsec log_n {logN} is outside the supported range {MinLogN}..{MaxLogN}.");
        }

        var salt = payload.Slice(2, SaltSize);
        var nonce = payload.Slice(2 + SaltSize, NonceSize);
        var keySecurityByte = payload[KeySecurityByteOffset];
        var ciphertext = payload[(KeySecurityByteOffset + 1)..];

        var symmetricKeyBytes = DeriveScryptKey(passphrase, salt, logN);
        try
        {
            using var key = Key.Import(AeadAlgorithm.XChaCha20Poly1305, symmetricKeyBytes, KeyBlobFormat.RawSymmetricKey);

            Span<byte> associatedData = [keySecurityByte];
            return AeadAlgorithm.XChaCha20Poly1305.Decrypt(key, nonce, associatedData, ciphertext)
                ?? throw new CryptographicException("NIP-49 decryption failed: wrong passphrase or corrupted ncryptsec.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(symmetricKeyBytes);
        }
    }

    /// <summary>
    /// Password is NFKC-normalized per the spec, so the same password entered on different
    /// platforms/keyboards derives the same key (official NIP-49 Unicode test vector).
    ///
    /// Uses BouncyCastle.Cryptography's raw <c>SCrypt.Generate</c> rather than
    /// NSec.Cryptography's <c>Scrypt</c>: NSec wraps libsodium's high-level
    /// <c>crypto_pwhash_scryptsalsa208sha256</c>, which hardcodes a 32-byte salt
    /// (<c>Scrypt.MinSaltSize == Scrypt.MaxSaltSize == 32</c>, confirmed by reflection),
    /// but NIP-49 specifies a 16-byte salt. BouncyCastle's generator takes the classic
    /// scrypt(password, salt, N, r, p, dkLen) signature with no salt-length constraint,
    /// and is what actually reproduces the spec's official decryption vector — NSec
    /// still does the XChaCha20-Poly1305 step, since its key/nonce/tag sizes (32/24/16)
    /// match NIP-49 exactly.
    ///
    /// Deliberate exception to "secrets stay in byte[]/Span, never string": Unicode
    /// normalization only has a <see cref="string"/> API, so the passphrase is briefly
    /// materialized as one managed string here (<c>.ToString().Normalize(...)</c>).
    /// Unlike <c>byte[]</c>, a <see cref="string"/> can't be explicitly zeroed and the
    /// runtime may have relocated/copied it before GC — this is a real, accepted gap in
    /// an otherwise zero-on-use design, scoped to exactly this one call, not a general
    /// license to pass passphrases as strings elsewhere in this module.
    /// </summary>
    private static byte[] DeriveScryptKey(ReadOnlyMemory<char> passphrase, ReadOnlySpan<byte> salt, byte logN)
    {
        var normalized = passphrase.ToString().Normalize(NormalizationForm.FormKC);
        var passwordBytes = Encoding.UTF8.GetBytes(normalized);
        try
        {
            return Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(
                passwordBytes, salt.ToArray(), 1 << logN, 8, 1, KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
