using Xunit;

namespace NightlyDawn.Core.Tests;

public class NoteEventBuilderTests
{
    private const string Author = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ParentAuthor = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string MentionedUser = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const long CreatedAt = 1_700_000_100;

    private static Note MakeNote(
        string id,
        string author,
        NoteKind kind = NoteKind.Text,
        string? rootId = null,
        IReadOnlyList<string>? mentioned = null,
        RelayUrl? firstSeenOnRelay = null) =>
        new(
            Id: id,
            AuthorPubkey: author,
            CreatedAt: 1_700_000_000,
            Kind: kind,
            Content: "parent content",
            Tags: [],
            RootId: rootId,
            MentionedPubkeys: mentioned,
            FirstSeenOnRelay: firstSeenOnRelay);

    [Fact]
    public void Post_ProducesKind1_WithNoTags()
    {
        var evt = NoteEventBuilder.Post("hello", CreatedAt);

        Assert.Equal((int)NoteKind.Text, evt.Kind);
        Assert.Equal("hello", evt.Content);
        Assert.Empty(evt.Tags);
    }

    [Fact]
    public void Reply_ToATopLevelNote_CarriesASingleRootMarkedTag_NotReply()
    {
        // NIP-10: "For top level replies (those replying directly to the root event), only the 'root'
        // marker should be used." The parent IS the root here, so there is nothing else to call it.
        var parent = MakeNote("parent1", ParentAuthor);

        var evt = NoteEventBuilder.Reply(parent, "hi", Author, CreatedAt);

        var eTags = evt.Tags.Where(t => t[0] == "e").ToList();
        Assert.Single(eTags);
        Assert.Equal(["e", "parent1", "", "root", ParentAuthor], eTags[0]);
    }

    [Fact]
    public void Reply_ToAnExistingReply_CarriesBothRootAndReplyETags()
    {
        var parent = MakeNote("parent2", ParentAuthor, rootId: "root1");

        var evt = NoteEventBuilder.Reply(parent, "hi", Author, CreatedAt);

        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "e", "root1", "", "root" }));
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "e", "parent2", "", "reply", ParentAuthor }));
    }

    [Fact]
    public void Reply_PTagsAreParentAuthorPlusMentions_DeduplicatedAndNeverTheReplyingAuthor()
    {
        var parent = MakeNote("parent3", ParentAuthor, mentioned: [MentionedUser, ParentAuthor, Author]);

        var evt = NoteEventBuilder.Reply(parent, "hi", Author, CreatedAt);

        var pTags = evt.Tags.Where(t => t[0] == "p").Select(t => t[1]).ToList();
        Assert.Equal([ParentAuthor, MentionedUser], pTags);
    }

    [Fact]
    public void Reply_UsesFirstSeenOnRelay_AsTheETagHint_WhenKnown()
    {
        var parent = MakeNote("parent4", ParentAuthor, firstSeenOnRelay: RelayUrl.Parse("wss://relay.example"));

        var evt = NoteEventBuilder.Reply(parent, "hi", Author, CreatedAt);

        var eTag = evt.Tags.Single(t => t[0] == "e");
        Assert.Equal("wss://relay.example/", eTag[2]);
    }

    [Theory]
    [InlineData(NoteKind.Repost)]
    [InlineData(NoteKind.GenericRepost)]
    public void Reply_Throws_WhenTheParentIsARepost(NoteKind kind)
    {
        var parent = MakeNote("repost1", ParentAuthor, kind: kind);

        var ex = Assert.Throws<ArgumentException>(() => NoteEventBuilder.Reply(parent, "hi", Author, CreatedAt));
        Assert.Contains(kind.ToString(), ex.Message);
    }

    [Fact]
    public void Repost_OfATextNote_ProducesKind6_WithAnETagAndAPTag_EmptyContent()
    {
        var target = MakeNote("note1", ParentAuthor);

        var evt = NoteEventBuilder.Repost(target, CreatedAt);

        Assert.Equal((int)NoteKind.Repost, evt.Kind);
        Assert.Equal(string.Empty, evt.Content);
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "e", "note1", "" }));
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "p", ParentAuthor }));
    }

    [Theory]
    [InlineData(NoteKind.Repost)]
    [InlineData(NoteKind.GenericRepost)]
    public void Repost_Throws_WhenTheTargetIsAlreadyARepost(NoteKind kind)
    {
        var target = MakeNote("repost2", ParentAuthor, kind: kind);

        var ex = Assert.Throws<ArgumentException>(() => NoteEventBuilder.Repost(target, CreatedAt));
        Assert.Contains(kind.ToString(), ex.Message);
    }

    [Fact]
    public void Reaction_ProducesKind7_WithEPKTags_AndPlusContent_ByDefault()
    {
        var target = MakeNote("note2", ParentAuthor);

        var evt = NoteEventBuilder.Reaction(target, CreatedAt);

        Assert.Equal(7, evt.Kind);
        Assert.Equal("+", evt.Content);
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "e", "note2", "" }));
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "p", ParentAuthor }));
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "k", "1" }));
    }

    [Fact]
    public void Reaction_KTagNamesTheTargetsActualKind_NotAlwaysText()
    {
        var target = MakeNote("note2b", ParentAuthor, kind: NoteKind.Repost);

        var evt = NoteEventBuilder.Reaction(target, CreatedAt);

        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "k", "6" }));
    }

    [Fact]
    public void Reaction_AcceptsACustomEmojiContent()
    {
        var target = MakeNote("note3", ParentAuthor);

        var evt = NoteEventBuilder.Reaction(target, CreatedAt, content: "\U0001F525");

        Assert.Equal("\U0001F525", evt.Content);
    }

    [Fact]
    public void Quote_ProducesKind1_WithAFourElementQTagAndAPTag()
    {
        var quoted = MakeNote("note4", ParentAuthor);

        var evt = NoteEventBuilder.Quote(quoted, "check this out", CreatedAt);

        Assert.Equal((int)NoteKind.Text, evt.Kind);
        Assert.Equal("check this out", evt.Content);
        // NIP-18 q-tag syntax: ["q", "<event-id>", "<relay-url>", "<pubkey-if-a-regular-event>"].
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "q", "note4", "", ParentAuthor }));
        Assert.Contains(evt.Tags, t => t.SequenceEqual(new[] { "p", ParentAuthor }));
    }
}
