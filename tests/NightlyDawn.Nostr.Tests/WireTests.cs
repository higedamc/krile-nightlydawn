using NightlyDawn.Core;
using NightlyDawn.Nostr.Tests.Fakes;
using NightlyDawn.Nostr.Wire;
using Xunit;

namespace NightlyDawn.Nostr.Tests;

public class WireTests
{
    [Fact]
    public void CanonicalSerialization_FollowsNip01Escaping()
    {
        var tags = new List<IReadOnlyList<string>> { new List<string> { "t", "nostr" } };
        var content = "line\nquote\"back\\slash\ttab\u0001 日本語 <>&";
        var bytes = NostrJson.CanonicalEventBytes("ab", 1, 1, tags, content);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        // Only the NIP-01 escapes; other control chars as \u00XX; non-ASCII and HTML-ish characters verbatim; no whitespace.
        Assert.Equal("[0,\"ab\",1,1,[[\"t\",\"nostr\"]],\"line\\nquote\\\"back\\\\slash\\ttab\\u0001 日本語 <>&\"]", json);
    }

    [Fact]
    public void CanonicalSerialization_RejectsLoneSurrogates_AndVerifierReportsMalformed()
    {
        var lone = "bad \uD800 text";
        Assert.Throws<ArgumentException>(() => NostrJson.CanonicalEventBytes("ab", 1, 1, [], lone));
        Assert.Throws<ArgumentException>(() => NostrJson.CanonicalEventBytes("ab", 1, 1, [["t", "\uDC00"]], "ok"));

        var signer = new TestSigner();
        var e = signer.Sign(1, "fine") with { Content = lone };
        Assert.Equal(VerificationResult.MalformedFields, EventVerifier.Verify(e));

        // A proper surrogate pair (astral plane) is fine and kept verbatim.
        var astral = "emoji \U0001F41D here";
        var json = System.Text.Encoding.UTF8.GetString(NostrJson.CanonicalEventBytes("ab", 1, 1, [], astral));
        Assert.Contains(astral, json);
    }

    [Fact]
    public void ParseRelayMessage_ReturnsNull_ForLoneSurrogateStrings()
    {
        Assert.Null(NostrJson.ParseRelayMessage("[\"EVENT\",\"s\",{\"id\":\"ab\",\"pubkey\":\"cd\",\"created_at\":1,\"kind\":1,\"tags\":[],\"content\":\"\\uD800\",\"sig\":\"ef\"}]"));
        Assert.Null(NostrJson.ParseRelayMessage("[\"NOTICE\",\"\\uDFFF\"]"));
        Assert.Null(NostrJson.ParseRelayMessage("[\"OK\",\"\\uD800\",true,\"\"]"));
    }

    [Fact]
    public void Verifier_AcceptsEventSignedWithTestKey_AndRejectsTampering()
    {
        var signer = new TestSigner();
        var e = signer.Sign(1, "hello", [["t", "x"]]);

        Assert.Equal(VerificationResult.Valid, EventVerifier.Verify(e));
        Assert.Equal(VerificationResult.IdMismatch, EventVerifier.Verify(e with { Content = "hell0" }));
        Assert.Equal(VerificationResult.IdMismatch, EventVerifier.Verify(e with { CreatedAt = e.CreatedAt + 1 }));

        // Correct id for the claimed pubkey, but the signature came from another key: impersonation attempt.
        var other = new TestSigner(2);
        var forged = e with { Pubkey = other.PubkeyHex, Id = EventVerifier.ComputeId(other.PubkeyHex, e.CreatedAt, 1, e.Tags, e.Content) };
        Assert.Equal(VerificationResult.InvalidSignature, EventVerifier.Verify(forged));

        Assert.Equal(VerificationResult.MalformedFields, EventVerifier.Verify(e with { Sig = "zz" }));
        Assert.Equal(VerificationResult.MalformedFields, EventVerifier.Verify(e with { Id = e.Id.ToUpperInvariant() }));
    }

    [Fact]
    public void RelayMessages_RoundTrip()
    {
        var signer = new TestSigner();
        var e = signer.Sign(1, "payload with \"quotes\" and \\ and \n");
        var parsed = NostrJson.ParseRelayMessage($"[\"EVENT\",\"sub1\",{FakeRelay.EventJson(e)}]");

        var evt = Assert.IsType<RelayMessage.Event>(parsed);
        Assert.Equal("sub1", evt.SubscriptionId);
        Assert.Equal(e.Id, evt.Payload.Id);
        Assert.Equal(e.Content, evt.Payload.Content);
        Assert.Equal(VerificationResult.Valid, EventVerifier.Verify(evt.Payload));

        Assert.IsType<RelayMessage.Eose>(NostrJson.ParseRelayMessage("[\"EOSE\",\"sub1\"]"));
        var ok = Assert.IsType<RelayMessage.Ok>(NostrJson.ParseRelayMessage("[\"OK\",\"abc\",false,\"blocked: nope\"]"));
        Assert.False(ok.Accepted);
        Assert.Equal("blocked: nope", ok.Reason);
        var notice = Assert.IsType<RelayMessage.Notice>(NostrJson.ParseRelayMessage("[\"NOTICE\",\"secret text\"]"));
        Assert.Equal("secret text".Length, notice.Length); // the text itself is not retained

        Assert.Null(NostrJson.ParseRelayMessage("not json"));
        Assert.Null(NostrJson.ParseRelayMessage("{\"a\":1}"));
        Assert.Null(NostrJson.ParseRelayMessage("[\"EVENT\",\"sub\",{\"id\":1}]"));
        Assert.Null(NostrJson.ParseRelayMessage("[\"WHATEVER\",\"x\"]"));
    }

    [Fact]
    public void ReqMessage_SerializesFilterWithTagFilters()
    {
        var filter = new NostrFilter(
            Authors: ["a"],
            Kinds: [1, 6],
            TagFilters: new Dictionary<string, IReadOnlyList<string>> { ["t"] = ["nostr"], ["#p"] = ["b"] },
            Since: 10,
            Limit: 5,
            Search: "q");
        var json = NostrJson.ReqMessage("s", filter);

        Assert.StartsWith("[\"REQ\",\"s\",{", json);
        Assert.Contains("\"authors\":[\"a\"]", json);
        Assert.Contains("\"kinds\":[1,6]", json);
        Assert.Contains("\"#t\":[\"nostr\"]", json);
        Assert.Contains("\"#p\":[\"b\"]", json);
        Assert.Contains("\"since\":10", json);
        Assert.Contains("\"limit\":5", json);
        Assert.Contains("\"search\":\"q\"", json);
        Assert.DoesNotContain("until", json);
    }
}
