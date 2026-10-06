using System.Text;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("NightlyDawn.Core.Tests")]

namespace NightlyDawn.Core;

/// <summary>
/// BIP-173 bech32 (not bech32m) encode/decode. Used for NIP-49's <c>ncryptsec1...</c>
/// (NightlyDawn.Keys) and NIP-19 identifiers like <c>npub1...</c> (display and the KQL
/// query compiler's <c>author:npub1...</c> form). Lives in Core rather than either
/// consumer for the same reason <see cref="NostrEventCanonicalization"/> does: this is a
/// protocol-level concern (NIP-19/NIP-49's wire encoding), not an implementation detail
/// of one leaf, and the alternative — <c>SignerDescriptor.DisplayLabel</c> carrying a
/// derived npub — would conflate a user-assigned label with a derived value that every
/// mechanical consumer (copy button, query compiler) would then have to reverse-parse.
///
/// Hand-rolled on Lead's explicit sign-off: unlike scrypt/XChaCha20-Poly1305
/// (confidentiality primitives that can fail silently), a bech32 bug fails loudly — a bad
/// checksum or a malleable decode is directly testable against BIP-173's own vectors, so
/// this is not the "never write your own crypto" class of risk.
///
/// Deliberate divergence from BIP-173: the spec caps the total string at 90 characters
/// (<see href="https://github.com/bitcoin/bips/blob/master/bip-0173.mediawiki"/>), but
/// Nostr's NIP-19/NIP-49 identifiers exceed that (<c>ncryptsec1...</c> is 162 characters).
/// This implementation does not enforce the 90-character cap; the test suite keeps
/// BIP-173's own overlong-string vector as a test proving we accept what BIP-173 would
/// reject, so the divergence stays visible rather than silently maintained.
///
/// Public surface is deliberately narrow (Lead's constraint): only <see cref="Encode"/>
/// and <see cref="TryDecode"/>. The 5-bit regrouping, checksum math, and the
/// exception-throwing <see cref="Decode"/> stay internal — <see cref="Decode"/> exists
/// so the test suite can assert *which* BIP-173 rule a given invalid vector violates
/// (distinct exception conditions), which a bool-returning API would collapse.
/// </summary>
public static class Bech32
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32Const = 1;

    private static readonly uint[] Generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    public static string Encode(string hrp, ReadOnlySpan<byte> payload)
    {
        var data = ConvertBits(payload, fromBits: 8, toBits: 5, pad: true)
            ?? throw new ArgumentException("Payload could not be converted to 5-bit groups.", nameof(payload));

        return EncodeFromFiveBitGroups(hrp, data);
    }

    /// <summary>Decodes a bech32 string whose human-readable part must equal <paramref name="expectedHrp"/>. Returns <see langword="false"/> (with <paramref name="payload"/> set to an empty array) for any malformed or checksum-invalid input, rather than throwing — this is the surface external callers (UI, query compiler) should use.</summary>
    public static bool TryDecode(string expectedHrp, string bech32, out byte[] payload)
    {
        try
        {
            payload = Decode(expectedHrp, bech32);
            return true;
        }
        catch (FormatException)
        {
            payload = [];
            return false;
        }
    }

    /// <summary>Encodes already-5-bit-grouped data directly, skipping the normal 8-to-5 conversion. A conforming encoder can never produce non-zero padding bits or a leftover-bit count that fails decoding, so this seam exists only so tests can construct the malformed inputs <see cref="Decode"/> must reject (B14-style malleability guards) without reaching into the checksum math by hand.</summary>
    internal static string EncodeFromFiveBitGroups(string hrp, List<int> data)
    {
        var checksum = CreateChecksum(hrp, data);

        var result = new StringBuilder(hrp.Length + 1 + data.Count + checksum.Length);
        result.Append(hrp).Append('1');
        foreach (var value in data)
        {
            result.Append(Charset[value]);
        }

        foreach (var value in checksum)
        {
            result.Append(Charset[value]);
        }

        return result.ToString();
    }

    /// <exception cref="FormatException">The string is not valid bech32 for <paramref name="expectedHrp"/>.</exception>
    internal static byte[] Decode(string expectedHrp, string bech32String)
    {
        // Printable US-ASCII only, and no mixed case (BIP-173: case-mixing is rejected
        // outright, not case-folded, because the checksum is defined over one case).
        var hasLower = false;
        var hasUpper = false;
        foreach (var c in bech32String)
        {
            if (c < 33 || c > 126)
            {
                throw new FormatException("bech32 string contains a character outside the printable US-ASCII range.");
            }

            hasLower |= c is >= 'a' and <= 'z';
            hasUpper |= c is >= 'A' and <= 'Z';
        }

        if (hasLower && hasUpper)
        {
            throw new FormatException("bech32 string mixes upper and lower case.");
        }

        var normalized = bech32String.ToLowerInvariant();
        var separator = normalized.LastIndexOf('1');

        // pos < 1: HRP must be non-empty. pos + 7 > length: need at least 6 checksum chars
        // after the separator (BIP-173's own length cap is intentionally not applied here).
        if (separator < 1 || separator + 7 > normalized.Length)
        {
            throw new FormatException("bech32 string has no valid separator position.");
        }

        var hrp = normalized[..separator];
        if (hrp != expectedHrp)
        {
            throw new FormatException($"Expected human-readable part '{expectedHrp}', got '{hrp}'.");
        }

        var dataChars = normalized[(separator + 1)..];
        var data = new List<int>(dataChars.Length);
        foreach (var c in dataChars)
        {
            var value = Charset.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException($"bech32 string contains a character not in the bech32 charset: '{c}'.");
            }

            data.Add(value);
        }

        if (!VerifyChecksum(hrp, data))
        {
            throw new FormatException("bech32 checksum is invalid.");
        }

        var payloadData = data.GetRange(0, data.Count - 6);

        var bytes = ConvertBits(payloadData, fromBits: 5, toBits: 8, pad: false)
            ?? throw new FormatException(
                "bech32 5-to-8-bit conversion failed: leftover bits were non-zero or too many to be padding (NIP-49, B14-style malleability guard).");

        var result = new byte[bytes.Count];
        for (var i = 0; i < bytes.Count; i++)
        {
            result[i] = (byte)bytes[i];
        }

        return result;
    }

    /// <summary>General power-of-2 base conversion (BIP-173 reference <c>convertbits</c>). Returns null when the input is invalid for the requested direction — for 5-to-8 decoding (<paramref name="pad"/> = false), that means non-zero padding bits or more leftover bits than padding allows.</summary>
    private static List<int>? ConvertBits(IEnumerable<int> data, int fromBits, int toBits, bool pad)
    {
        var accumulator = 0;
        var bits = 0;
        var result = new List<int>();
        var maxValue = (1 << toBits) - 1;
        var maxAccumulator = (1 << (fromBits + toBits - 1)) - 1;

        foreach (var value in data)
        {
            if (value < 0 || value >> fromBits != 0)
            {
                return null;
            }

            accumulator = ((accumulator << fromBits) | value) & maxAccumulator;
            bits += fromBits;
            while (bits >= toBits)
            {
                bits -= toBits;
                result.Add((accumulator >> bits) & maxValue);
            }
        }

        if (pad)
        {
            if (bits > 0)
            {
                result.Add((accumulator << (toBits - bits)) & maxValue);
            }
        }
        else if (bits >= fromBits || ((accumulator << (toBits - bits)) & maxValue) != 0)
        {
            return null;
        }

        return result;
    }

    private static List<int>? ConvertBits(ReadOnlySpan<byte> data, int fromBits, int toBits, bool pad)
    {
        var values = new int[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            values[i] = data[i];
        }

        return ConvertBits(values, fromBits, toBits, pad);
    }

    private static uint Polymod(IEnumerable<int> values)
    {
        uint checksum = 1;
        foreach (var value in values)
        {
            var top = checksum >> 25;
            checksum = ((checksum & 0x1ffffff) << 5) ^ (uint)value;
            for (var i = 0; i < 5; i++)
            {
                if (((top >> i) & 1) != 0)
                {
                    checksum ^= Generator[i];
                }
            }
        }

        return checksum;
    }

    private static List<int> HrpExpand(string hrp)
    {
        var result = new List<int>(hrp.Length * 2 + 1);
        foreach (var c in hrp)
        {
            result.Add(c >> 5);
        }

        result.Add(0);
        foreach (var c in hrp)
        {
            result.Add(c & 31);
        }

        return result;
    }

    private static bool VerifyChecksum(string hrp, List<int> data) =>
        Polymod(HrpExpand(hrp).Concat(data)) == Bech32Const;

    private static int[] CreateChecksum(string hrp, List<int> data)
    {
        var values = HrpExpand(hrp).Concat(data).Concat([0, 0, 0, 0, 0, 0]);
        var polymod = Polymod(values) ^ Bech32Const;

        var checksum = new int[6];
        for (var i = 0; i < 6; i++)
        {
            checksum[i] = (int)((polymod >> (5 * (5 - i))) & 31);
        }

        return checksum;
    }
}
