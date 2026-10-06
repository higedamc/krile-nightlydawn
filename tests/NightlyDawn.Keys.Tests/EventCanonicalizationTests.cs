using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.Keys.Tests;

public class EventCanonicalizationTests
{
    // Independently generated via Python's json.dumps(arr, separators=(',', ':'),
    // ensure_ascii=False) + hashlib.sha256 — a from-scratch reference, not derived from
    // this implementation, so this test can't pass merely by being self-consistent.
    private const string Pubkey = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const long CreatedAt = 1700000000;
    private const int Kind = 1;

    private static readonly IReadOnlyList<IReadOnlyList<string>> Tags =
    [
        ["e", "aaaa"],
        ["p", "bbbb", "wss://relay.example"],
    ];

    private const string Content =
        "hello \"world\"\nwith unicode: café 日本語 and backslash \\ and tab\t.";

    private const string ExpectedCanonical =
        "[0,\"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\",1700000000,1,[[\"e\",\"aaaa\"],[\"p\",\"bbbb\",\"wss://relay.example\"]],\"hello \\\"world\\\"\\nwith unicode: café 日本語 and backslash \\\\ and tab\\t.\"]";

    private const string ExpectedSha256Hex = "46fd523a3cfabc7f26a297bb3ac23e7e529cf8331ea5b279244af42b617e3687";

    [Fact]
    public void Serialize_MatchesIndependentPythonReference()
    {
        var canonical = NostrEventCanonicalization.Serialize(Pubkey, CreatedAt, Kind, Tags, Content);

        Assert.Equal(ExpectedCanonical, canonical);
    }

    [Fact]
    public void ComputeId_MatchesIndependentPythonReference()
    {
        var id = NostrEventCanonicalization.ComputeId(Pubkey, CreatedAt, Kind, Tags, Content);

        Assert.Equal(ExpectedSha256Hex, Convert.ToHexString(id).ToLowerInvariant());
    }

    [Fact]
    public void ComputeId_RejectsUnpairedHighSurrogate()
    {
        var contentWithLoneSurrogate = "before" + char.ConvertFromUtf32(0x1F600)[0] + "after";

        Assert.Throws<ArgumentException>(() =>
            NostrEventCanonicalization.ComputeId(Pubkey, CreatedAt, Kind, Tags, contentWithLoneSurrogate));
    }

    [Fact]
    public void Serialize_EscapesControlCharactersOutsideTheNamedSet()
    {
        var withControlChar = "a" + (char)0x01 + "b";

        var canonical = NostrEventCanonicalization.Serialize(Pubkey, CreatedAt, Kind, Tags, withControlChar);

        Assert.Contains("a\\u0001b", canonical);
    }

    [Fact]
    public void Serialize_DoesNotEscapeNonAsciiAsUnicodeEscapes()
    {
        var canonical = NostrEventCanonicalization.Serialize(Pubkey, CreatedAt, Kind, Tags, Content);

        Assert.Contains("café", canonical);
        Assert.DoesNotContain("\\u00e9", canonical, StringComparison.OrdinalIgnoreCase);
    }
}
