using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.Keys.Tests;

/// <summary>BIP-173's own official test vectors (https://github.com/bitcoin/bips/blob/master/bip-0173.mediawiki#test-vectors), plus two tests isolating the padding guards Lead called out, and one proving the deliberate 90-character-cap divergence stays visible.</summary>
public class Bech32Tests
{
    [Theory]
    [InlineData("a", "A12UEL5L")]
    [InlineData("a", "a12uel5l")]
    [InlineData(
        "an83characterlonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio",
        "an83characterlonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio1tt5tgs")]
    [InlineData("abcdef", "abcdef1qpzry9x8gf2tvdw0s3jn54khce6mua7lmqqqxw")]
    [InlineData(
        "1",
        "11qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqc8247j")]
    [InlineData("split", "split1checkupstagehandshakeupstreamerranterredcaperred2y9e3w")]
    [InlineData("?", "?1ezyfcl")]
    public void Decode_AcceptsOfficialValidVectors(string hrp, string bech32String)
    {
        // Must not throw; the exact decoded bytes aren't asserted here since several of
        // these vectors (e.g. the all-'q' one) exist purely to test checksum math, not
        // to carry a meaningful payload.
        Bech32.Decode(hrp, bech32String);
    }

    [Fact]
    public void Decode_RejectsOfficialInvalidVector_NoSeparator()
    {
        Assert.Throws<FormatException>(() => Bech32.Decode("pzry9x0s0muk", "pzry9x0s0muk"));
    }

    [Theory]
    [InlineData("1pzry9x0s0muk")]
    [InlineData("10a06t8")]
    [InlineData("1qzzfhee")]
    public void Decode_RejectsOfficialInvalidVector_EmptyHrp(string bech32String)
    {
        Assert.Throws<FormatException>(() => Bech32.Decode("", bech32String));
    }

    [Fact]
    public void Decode_RejectsOfficialInvalidVector_InvalidDataCharacter()
    {
        Assert.Throws<FormatException>(() => Bech32.Decode("x", "x1b4n0q5v"));
    }

    [Fact]
    public void Decode_RejectsOfficialInvalidVector_TooShortChecksum()
    {
        Assert.Throws<FormatException>(() => Bech32.Decode("li", "li1dgmt3"));
    }

    [Fact]
    public void Decode_RejectsOfficialInvalidVector_InvalidCharacterInChecksum()
    {
        var withInvalidChar = "de1lg7wt" + char.ConvertFromUtf32(0xFF);
        Assert.Throws<FormatException>(() => Bech32.Decode("de", withInvalidChar));
    }

    [Fact]
    public void Decode_RejectsOfficialInvalidVector_ChecksumComputedWithUppercaseHrp()
    {
        // "A1G7SGD8": the checksum was computed against the HRP "A" rather than the
        // canonical lowercase "a", so a correct decoder (which always lowercases before
        // checksum verification) must reject it as a checksum mismatch.
        Assert.Throws<FormatException>(() => Bech32.Decode("a", "A1G7SGD8"));
    }

    [Fact]
    public void Decode_RejectsOfficialInvalidVector_HrpCharacterOutOfRange()
    {
        var belowRange = char.ConvertFromUtf32(0x20) + "1nwldj5";
        var atDel = char.ConvertFromUtf32(0x7F) + "1axkwrx";
        var aboveAscii = char.ConvertFromUtf32(0x80) + "1eym55h";

        Assert.Throws<FormatException>(() => Bech32.Decode("irrelevant", belowRange));
        Assert.Throws<FormatException>(() => Bech32.Decode("irrelevant", atDel));
        Assert.Throws<FormatException>(() => Bech32.Decode("irrelevant", aboveAscii));
    }

    [Fact]
    public void Decode_RejectsMixedCase()
    {
        // tb1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3q0sL5k7 (official "Mixed case" vector)
        Assert.Throws<FormatException>(() => Bech32.Decode(
            "tb", "tb1qrp33g0q5c5txsp9arysrx4k6zdkfs4nce4xj0gdcccefvpysxf3q0sL5k7"));
    }

    [Fact]
    public void Decode_AcceptsOverlongString_DeliberateDivergenceFromBip173()
    {
        // BIP-173's own "overall max length exceeded" invalid vector — 91 characters,
        // otherwise checksum-valid. BIP-173 rejects it purely for length; this codec does
        // not enforce that cap, because NIP-19/NIP-49 identifiers are routinely longer
        // than 90 characters (see the class doc). This test exists so that acceptance is
        // a visible, asserted choice rather than a silently-missing check.
        const string vector =
            "an84characterslonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio1569pvx";

        var exception = Record.Exception(() => Bech32.Decode(
            "an84characterslonghumanreadablepartthatcontainsthenumber1andtheexcludedcharactersbio", vector));

        Assert.Null(exception);
    }

    [Fact]
    public void Decode_RejectsNonZeroPaddingBits()
    {
        // Hand-built 5-bit groups [0, 1]: a conforming encoder could never produce this —
        // the real payload bits are all zero, but the low 2 padding bits of the final
        // group are set to 1. This is exactly the malleability the B14 discussion in
        // Core's RelayUrl review was about, applied to bech32's own padding rule.
        var crafted = Bech32.EncodeFromFiveBitGroups("x", [0, 1]);

        Assert.Throws<FormatException>(() => Bech32.Decode("x", crafted));
    }

    [Fact]
    public void Decode_RejectsTooManyLeftoverBits()
    {
        // Three all-zero 5-bit groups (15 bits) leave 7 leftover bits after 8-bit
        // emission — more than fromBits (5), so the reference algorithm's other guard
        // (distinct from the non-zero-padding check) must fire even though every bit is 0.
        var crafted = Bech32.EncodeFromFiveBitGroups("x", [0, 0, 0]);

        Assert.Throws<FormatException>(() => Bech32.Decode("x", crafted));
    }

    [Fact]
    public void EncodeThenDecode_RoundTrips()
    {
        byte[] payload = [0x00, 0x01, 0x02, 0xff, 0xfe, 0x7f];

        var encoded = Bech32.Encode("ncryptsec", payload);
        var decoded = Bech32.Decode("ncryptsec", encoded);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void Decode_AcceptsNcryptsecLength_ExceedingBip173Cap()
    {
        // NIP-49's ncryptsec1... is 162 characters (91-byte payload); BIP-173 caps at 90.
        byte[] payload = new byte[91];

        var encoded = Bech32.Encode("ncryptsec", payload);

        Assert.True(encoded.Length > 90);
        Assert.Equal(payload, Bech32.Decode("ncryptsec", encoded));
    }
}
