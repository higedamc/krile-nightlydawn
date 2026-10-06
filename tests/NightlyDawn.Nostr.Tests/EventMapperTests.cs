using NightlyDawn.Core;
using NightlyDawn.Nostr.Mapping;
using NightlyDawn.Nostr.Tests.Fakes;
using Xunit;

namespace NightlyDawn.Nostr.Tests;

public class EventMapperTests
{
    private static readonly TestSigner Signer = new();
    private static readonly EventMapper Mapper = new();
    private const string Root = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string Reply = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Quoted = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string Friend = "4444444444444444444444444444444444444444444444444444444444444444";

    [Fact]
    public void ToNote_UsesNip10Markers_WhenPresent()
    {
        var e = Signer.Sign(1, "re", [["e", Root, "", "root"], ["e", Reply, "wss://r", "reply"], ["p", Friend], ["t", "Nostr"], ["t", "nostr"], ["q", Quoted]]);
        var note = Mapper.ToNote(e);

        Assert.Equal(NoteKind.Text, note.Kind);
        Assert.Equal(Root, note.RootId);
        Assert.Equal(Reply, note.ReplyId);
        Assert.Equal(Quoted, note.QuotedNoteId);
        Assert.Equal([Friend], note.MentionedPubkeys);
        Assert.Equal(["nostr"], note.Hashtags); // lower-cased, deduped
        Assert.Null(note.RepostedNoteId);
        Assert.Same(e.Tags, note.Tags);
    }

    [Fact]
    public void ToNote_FallsBackToPositionalETags_AndSkipsNonHexIds()
    {
        var e = Signer.Sign(1, "re", [["e", Root], ["e", "not-an-id"], ["e", Reply]]);
        var note = Mapper.ToNote(e);

        Assert.Equal(Root, note.RootId);
        Assert.Equal(Reply, note.ReplyId);

        var single = Mapper.ToNote(Signer.Sign(1, "re", [["e", Root]]));
        Assert.Equal(Root, single.RootId);
        Assert.Equal(Root, single.ReplyId);

        var top = Mapper.ToNote(Signer.Sign(1, "top level"));
        Assert.Null(top.RootId);
        Assert.Null(top.ReplyId);
    }

    [Fact]
    public void ToNote_MapsRepostTarget_ForKind6And16()
    {
        var repost = Mapper.ToNote(Signer.Sign(6, "", [["e", Root, "wss://r"], ["p", Friend]]));
        Assert.Equal(NoteKind.Repost, repost.Kind);
        Assert.Equal(Root, repost.RepostedNoteId);
        Assert.Null(repost.RootId);

        var generic = Mapper.ToNote(Signer.Sign(16, "", [["e", Reply], ["k", "30023"]]));
        Assert.Equal(NoteKind.GenericRepost, generic.Kind);
        Assert.Equal(Reply, generic.RepostedNoteId);
    }

    [Fact]
    public void ToNote_RejectsNonNoteKinds()
    {
        Assert.Throws<EventMappingException>(() => Mapper.ToNote(Signer.Sign(0, "{}")));
        Assert.Throws<EventMappingException>(() => Mapper.ToNote(Signer.Sign(7, "+")));
    }

    [Fact]
    public void ToProfile_ParsesKnownFields_AndDropsNonHttpImageUrls()
    {
        var content = "{\"name\":\"fizzy\",\"display_name\":\"Fizzy Bee\",\"about\":\"maker\",\"picture\":\"javascript:alert(1)\",\"banner\":\"https://img.example/b.png\",\"nip05\":\"fizzy@example.com\",\"lud16\":\"fizzy@wallet.example\",\"unknown\":1}";
        var p = Mapper.ToProfile(Signer.Sign(0, content, createdAt: 42));

        Assert.Equal(Signer.PubkeyHex, p.Pubkey);
        Assert.Equal("fizzy", p.Name);
        Assert.Equal("Fizzy Bee", p.DisplayName);
        Assert.Equal("maker", p.About);
        Assert.Null(p.Picture);
        Assert.Equal("https://img.example/b.png", p.Banner);
        Assert.Equal("fizzy@example.com", p.Nip05);
        Assert.Equal("fizzy@wallet.example", p.Lud16);
        Assert.Equal(42, p.UpdatedAt);
    }

    [Fact]
    public void ToProfile_ThrowsOnMalformedContent_AndCountsIt()
    {
        var diagnostics = new NostrBackendDiagnostics();
        var mapper = new EventMapper(diagnostics);

        Assert.Throws<EventMappingException>(() => mapper.ToProfile(Signer.Sign(0, "not json")));
        Assert.Throws<EventMappingException>(() => mapper.ToProfile(Signer.Sign(0, "[1,2]")));
        Assert.Throws<EventMappingException>(() => mapper.ToProfile(Signer.Sign(0, "")));
        Assert.Throws<EventMappingException>(() => mapper.ToProfile(Signer.Sign(1, "{}")));
        Assert.Equal(4, diagnostics.MappingFailures);
    }

    [Fact]
    public void ToRelayList_SplitsReadWrite_AndDropsInsecureOrJunkEntries()
    {
        var e = Signer.Sign(10002, "", [["r", "wss://both.example"], ["r", "wss://read.example", "read"], ["r", "wss://write.example/", "write"], ["r", "ws://plain.example"], ["r", "nonsense"], ["r", "wss://both.example"]]);
        var list = Mapper.ToRelayList(e);

        Assert.Equal(Signer.PubkeyHex, list.Pubkey);
        Assert.Equal(["wss://both.example", "wss://read.example"], list.ReadRelays.Select(r => r.Value));
        Assert.Equal(["wss://both.example", "wss://write.example"], list.WriteRelays.Select(r => r.Value));

        Assert.Throws<EventMappingException>(() => Mapper.ToRelayList(Signer.Sign(3, "")));
    }
}
