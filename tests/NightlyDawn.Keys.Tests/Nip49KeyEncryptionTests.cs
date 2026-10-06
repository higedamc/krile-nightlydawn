using System.Security.Cryptography;
using System.Text;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.Keys.Tests;

public class Nip49KeyEncryptionTests
{
    // https://github.com/nostr-protocol/nips/blob/master/49.md#decryption
    private const string OfficialNcryptsec =
        "ncryptsec1qgg9947rlpvqu76pj5ecreduf9jxhselq2nae2kghhvd5g7dgjtcxfqtd67p9m0w57lspw8gsq6yphnm8623nsl8xn9j4jdzz84zm3frztj3z7s35vpzmqf6ksu8r89qk5z2zxfmu5gv8th8wclt0h4p";

    private const string OfficialPassword = "nostr";

    private const string OfficialExpectedPrivateKeyHex =
        "3501454135014541350145413501453fefb02227e449e57cf4d3a3ce05378683";

    [Fact]
    public void Decrypt_MatchesOfficialNip49TestVector()
    {
        // A self-made encrypt-then-decrypt round trip would pass even if this
        // implementation disagreed with every other Nostr client on the wire format —
        // only the spec's own vector proves interoperability (Lead's instruction).
        var payload = Bech32.Decode("ncryptsec", OfficialNcryptsec);

        var decrypted = Nip49KeyEncryption.Decrypt(payload, OfficialPassword.AsMemory());

        Assert.Equal(Convert.FromHexString(OfficialExpectedPrivateKeyHex), decrypted);
    }

    [Fact]
    public void OfficialNip49Vector_EmbedsLogN16()
    {
        var payload = Bech32.Decode("ncryptsec", OfficialNcryptsec);

        Assert.Equal(91, payload.Length);
        Assert.Equal(0x02, payload[0]);
        Assert.Equal(16, payload[1]);
    }

    [Fact]
    public void PasswordNormalization_MatchesOfficialNip49UnicodeVector()
    {
        // https://github.com/nostr-protocol/nips/blob/master/49.md#password-unicode-normalization
        // Input codepoints:    U+212B U+2126 U+1E9B U+0323
        // Expected NFKC form:  U+00C5 U+03A9 U+1E69
        var input = "ÅΩẛ̣";
        var expected = "ÅΩṩ";

        Assert.Equal(expected, input.Normalize(NormalizationForm.FormKC));
    }

    [Fact]
    public void EncryptThenDecrypt_RoundTrips()
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);

        var payload = Nip49KeyEncryption.Encrypt(
            privateKey, "correct-horse-battery-staple".AsMemory(), logN: 4, KeySecurity.Unknown);

        var decrypted = Nip49KeyEncryption.Decrypt(payload, "correct-horse-battery-staple".AsMemory());

        Assert.Equal(privateKey, decrypted);
    }

    [Fact]
    public void Decrypt_WithWrongPassphrase_ThrowsCryptographicException()
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);

        var payload = Nip49KeyEncryption.Encrypt(privateKey, "right-password".AsMemory(), logN: 4, KeySecurity.Unknown);

        Assert.Throws<CryptographicException>(() =>
            Nip49KeyEncryption.Decrypt(payload, "wrong-password".AsMemory()));
    }

    [Fact]
    public void Decrypt_WithTamperedCiphertext_ThrowsCryptographicException()
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);

        var payload = Nip49KeyEncryption.Encrypt(privateKey, "a-password".AsMemory(), logN: 4, KeySecurity.Unknown);
        payload[^1] ^= 0xFF; // flip a bit in the Poly1305 tag

        Assert.Throws<CryptographicException>(() =>
            Nip49KeyEncryption.Decrypt(payload, "a-password".AsMemory()));
    }

    [Fact]
    public void Encrypt_ProducesDifferentCiphertextEachTime_DueToRandomSaltAndNonce()
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);

        var first = Nip49KeyEncryption.Encrypt(privateKey, "same-password".AsMemory(), logN: 4, KeySecurity.Unknown);
        var second = Nip49KeyEncryption.Encrypt(privateKey, "same-password".AsMemory(), logN: 4, KeySecurity.Unknown);

        Assert.NotEqual(first, second);
    }

    // B2: log_n comes from a pasted-in ncryptsec string — untrusted input. Unvalidated,
    // 1 << logN either exhausts memory (e.g. logN=23 asks scrypt for 8 GiB+ at r=8) or,
    // at 31/32, hits C#'s int-shift 5-bit masking and produces a negative/wrapped N.
    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(255)]
    public void Decrypt_RejectsOutOfRangeLogN_BeforeTouchingScrypt(byte maliciousLogN)
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);
        var payload = Nip49KeyEncryption.Encrypt(privateKey, "pw".AsMemory(), logN: 4, KeySecurity.Unknown);
        payload[1] = maliciousLogN;

        Assert.Throws<FormatException>(() => Nip49KeyEncryption.Decrypt(payload, "pw".AsMemory()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    [InlineData(255)]
    public void Encrypt_RejectsOutOfRangeLogN(byte maliciousLogN)
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Nip49KeyEncryption.Encrypt(privateKey, "pw".AsMemory(), maliciousLogN, KeySecurity.Unknown));
    }
}
