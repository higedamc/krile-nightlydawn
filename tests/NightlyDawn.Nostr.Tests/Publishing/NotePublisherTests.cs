using System.Text;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Publishing;
using NightlyDawn.Nostr.Tests.Fakes;
using Xunit;

namespace NightlyDawn.Nostr.Tests.Publishing;

public sealed class NotePublisherTests
{
    private static readonly DateTimeOffset FixedNow = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly FakeNostrBackend _backend = new();
    private readonly FakeKeyStore _keyStore = new();
    private readonly FakeEventMapper _mapper = new();
    private readonly NotePublisher _sut;

    public NotePublisherTests() =>
        _sut = new NotePublisher(_backend, _keyStore, _mapper, new FixedTimeProvider(FixedNow));

    private static Note TextNote(string id = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", string author = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2") =>
        new(id, author, 1_700_000_000, NoteKind.Text, "hello", []);

    private static Note RepostNote() =>
        new("c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3", "d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4", 1_700_000_001, NoteKind.Repost, "", []);

    // -- PostNoteAsync --------------------------------------------------

    [Fact]
    public async Task PostNoteAsync_SignsMapsAndPublishes_ReturnsPublishedNote()
    {
        var published = await _sut.PostNoteAsync("hello world");

        Assert.Equal(_backend.PublishedEvents.Single().Id, published.Note.Id);
        Assert.True(published.PublishResult.AnyAccepted);
        Assert.Equal(1, _backend.PublishedEvents.Single().Kind);
        Assert.Equal("hello world", _backend.PublishedEvents.Single().Content);
    }

    [Fact]
    public async Task PostNoteAsync_UsesTimeProvider_ForCreatedAt()
    {
        await _sut.PostNoteAsync("hello");

        Assert.Equal(FixedNow.ToUnixTimeSeconds(), _backend.PublishedEvents.Single().CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PostNoteAsync_RejectsEmptyOrWhitespaceContent(string content)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.PostNoteAsync(content));
        Assert.Empty(_backend.PublishedEvents);
    }

    [Fact]
    public async Task PostNoteAsync_RejectsNullContent()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.PostNoteAsync(null!));
    }

    [Fact]
    public async Task PostNoteAsync_RejectsContentOverMaxBytes()
    {
        var tooLong = new string('x', NotePublisher.MaxContentBytes + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.PostNoteAsync(tooLong));
        Assert.Contains(NotePublisher.MaxContentBytes.ToString(), ex.Message);
        Assert.Empty(_backend.PublishedEvents);
    }

    [Fact]
    public async Task PostNoteAsync_AllowsAsciiContentAtExactlyMaxBytes()
    {
        var exact = new string('x', NotePublisher.MaxContentBytes);

        await _sut.PostNoteAsync(exact);

        Assert.Single(_backend.PublishedEvents);
    }

    [Fact]
    public async Task PostNoteAsync_RejectsACjkBody_UnderTheRetiredCharCapButOverTheByteCap()
    {
        // Plan §9.5 blocking ①: every char here is a 3-byte CJK ideograph, so 50,000 of them is under the
        // retired 65,536-*character* cap but 150,000 UTF-8 bytes -- well over MaxContentBytes (126,976). A
        // char-counting guard would have let this through and had every relay reject it post-signing.
        const int charCount = 50_000;
        var cjk = new string('漢', charCount);
        Assert.True(charCount <= 65_536);
        Assert.True(Encoding.UTF8.GetByteCount(cjk) > NotePublisher.MaxContentBytes);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.PostNoteAsync(cjk));
        Assert.Contains("UTF-8 bytes", ex.Message);
        Assert.Empty(_backend.PublishedEvents);
    }

    [Fact]
    public async Task PostNoteAsync_AllowsAnAsciiBody_AtTheSameCharCountTheCjkBodyAboveWasRejectedAt()
    {
        // Same 50,000 *characters* as the CJK test above, but ASCII is 1 byte/char so this body is 50,000
        // UTF-8 bytes -- comfortably under MaxContentBytes. This is the asymmetry the old char-count guard
        // missed: the exact same "how much did I type" produces a passing body in one language and a
        // rejected one in another.
        var ascii = new string('x', 50_000);
        Assert.True(Encoding.UTF8.GetByteCount(ascii) <= NotePublisher.MaxContentBytes);

        await _sut.PostNoteAsync(ascii);

        Assert.Single(_backend.PublishedEvents);
    }

    [Fact]
    public async Task PostNoteAsync_WhenZeroRelaysAccept_PublishExceptionPropagatesUnswallowed()
    {
        var rejection = new EventPublishException(new PublishResult([]));
        _backend.RejectWith = rejection;

        var thrown = await Assert.ThrowsAsync<EventPublishException>(() => _sut.PostNoteAsync("hello"));
        Assert.Same(rejection, thrown);
    }

    [Fact]
    public async Task PostNoteAsync_DoesNotPublish_WhenMappingThrows()
    {
        _mapper.ThrowOnToNote = new EventMappingException("boom");

        await Assert.ThrowsAsync<EventMappingException>(() => _sut.PostNoteAsync("hello"));

        // The negative control for plan §9.2-1 (map before publish): this assertion was verified to go red
        // (PublishedEvents.Count becomes 1) when SignMapPublishAsync's map and publish calls are swapped,
        // confirming the ordering actually matters rather than being untested.
        Assert.Empty(_backend.PublishedEvents);
    }

    // -- ReplyToAsync -----------------------------------------------------

    [Fact]
    public async Task ReplyToAsync_ToTopLevelParent_CarriesSingleRootTag()
    {
        var parent = TextNote();

        await _sut.ReplyToAsync(parent, "a reply");

        var tags = _backend.PublishedEvents.Single().Tags;
        Assert.Contains(tags, t => t[0] == "e" && t[1] == parent.Id && t[3] == "root");
        Assert.Contains(tags, t => t[0] == "p" && t[1] == parent.AuthorPubkey);
    }

    [Fact]
    public async Task ReplyToAsync_ExcludesReplyingAuthorFromMentions()
    {
        var self = _keyStore.ActiveSigner.PubkeyHex;
        var parent = TextNote(author: self);

        await _sut.ReplyToAsync(parent, "a reply");

        var tags = _backend.PublishedEvents.Single().Tags;
        Assert.DoesNotContain(tags, t => t[0] == "p" && t[1] == self);
    }

    [Fact]
    public async Task ReplyToAsync_ReadsActiveSigner_EachCall_NotCached()
    {
        var firstSigner = _keyStore.ActiveSigner;
        var parent = TextNote();

        await _sut.ReplyToAsync(parent, "first");
        _keyStore.ActiveSigner = new TestSigner(seed: 9);
        await _sut.ReplyToAsync(parent, "second");

        Assert.Equal(2, _keyStore.GetActiveSignerCallCount);
        Assert.NotEqual(firstSigner.PubkeyHex, _backend.PublishedEvents[1].Pubkey);
        Assert.Equal(_keyStore.ActiveSigner.PubkeyHex, _backend.PublishedEvents[1].Pubkey);
    }

    [Fact]
    public async Task ReplyToAsync_RejectsRepostParent()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.ReplyToAsync(RepostNote(), "x"));
        Assert.Empty(_backend.PublishedEvents);
    }

    [Fact]
    public async Task ReplyToAsync_RejectsNullParent()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.ReplyToAsync(null!, "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ReplyToAsync_RejectsEmptyOrWhitespaceContent(string content)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.ReplyToAsync(TextNote(), content));
    }

    // -- RepostAsync ------------------------------------------------------

    [Fact]
    public async Task RepostAsync_CarriesETagAndPTag_EmptyContent()
    {
        var target = TextNote();

        var published = await _sut.RepostAsync(target);

        var signed = _backend.PublishedEvents.Single();
        Assert.Equal(6, signed.Kind);
        Assert.Equal(string.Empty, signed.Content);
        Assert.Contains(signed.Tags, t => t[0] == "e" && t[1] == target.Id);
        Assert.Contains(signed.Tags, t => t[0] == "p" && t[1] == target.AuthorPubkey);
        Assert.True(published.PublishResult.AnyAccepted);
    }

    [Fact]
    public async Task RepostAsync_RejectsRepostTarget()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.RepostAsync(RepostNote()));
        Assert.Empty(_backend.PublishedEvents);
    }

    [Fact]
    public async Task RepostAsync_RejectsNullTarget()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.RepostAsync(null!));
    }

    // -- QuoteAsync -------------------------------------------------------

    [Fact]
    public async Task QuoteAsync_CarriesQTagAndPTag()
    {
        var quoted = TextNote();

        await _sut.QuoteAsync(quoted, "look at this");

        var signed = _backend.PublishedEvents.Single();
        Assert.Equal(1, signed.Kind);
        Assert.Contains(signed.Tags, t => t[0] == "q" && t[1] == quoted.Id);
        Assert.Contains(signed.Tags, t => t[0] == "p" && t[1] == quoted.AuthorPubkey);
    }

    [Fact]
    public async Task QuoteAsync_AllowsQuotingARepost()
    {
        // NoteEventBuilder.Quote has no RequireTextKind check, unlike Reply/Repost -- quoting a repost is
        // a legitimate action (sharing a retweet with commentary), so this must not throw.
        await _sut.QuoteAsync(RepostNote(), "sharing this");

        Assert.Single(_backend.PublishedEvents);
    }

    [Fact]
    public async Task QuoteAsync_RejectsNullQuoted()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.QuoteAsync(null!, "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task QuoteAsync_RejectsEmptyOrWhitespaceContent(string content)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.QuoteAsync(TextNote(), content));
    }

    // -- ReactAsync -------------------------------------------------------

    [Fact]
    public async Task ReactAsync_ReturnsPublishResult_NotAMappedNote()
    {
        var target = TextNote();

        var result = await _sut.ReactAsync(target);

        Assert.IsType<PublishResult>(result);
        Assert.True(result.AnyAccepted);
        Assert.Equal(0, _mapper.ToNoteCallCount); // kind:7 can never become a Note (plan §9.1) -- never mapped
    }

    [Fact]
    public async Task ReactAsync_CarriesETagPTagAndKTag_DefaultContentIsPlus()
    {
        var target = TextNote();

        await _sut.ReactAsync(target);

        var signed = _backend.PublishedEvents.Single();
        Assert.Equal(7, signed.Kind);
        Assert.Equal("+", signed.Content);
        Assert.Contains(signed.Tags, t => t[0] == "e" && t[1] == target.Id);
        Assert.Contains(signed.Tags, t => t[0] == "p" && t[1] == target.AuthorPubkey);
        Assert.Contains(signed.Tags, t => t[0] == "k" && t[1] == "1");
    }

    [Fact]
    public async Task ReactAsync_AcceptsACustomEmojiContent()
    {
        await _sut.ReactAsync(TextNote(), "\U0001F525"); // 🔥

        Assert.Equal("\U0001F525", _backend.PublishedEvents.Single().Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReactAsync_RejectsEmptyOrWhitespaceContent(string content)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.ReactAsync(TextNote(), content));
    }

    [Fact]
    public async Task ReactAsync_RejectsContentOverMaxLength()
    {
        var tooLong = new string('+', NotePublisher.MaxReactionContentLength + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.ReactAsync(TextNote(), tooLong));
        Assert.Contains(NotePublisher.MaxReactionContentLength.ToString(), ex.Message);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("a\nb")]
    [InlineData("\t")]
    [InlineData("")] // bell -- an arbitrary non-printable control character
    public async Task ReactAsync_RejectsControlCharacters_ThisIsTheArbitraryUiInputGuard(string content)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.ReactAsync(TextNote(), content));
        Assert.Empty(_backend.PublishedEvents);
    }

    // Plan §9.5 blocking ②: these all slipped past the old Rune.IsControl-only check (C0/C1 only) and past
    // IsNullOrWhiteSpace (which only catches U+2028/U+2029 when they are the *entire* string, not embedded).
    // Built with char.ConvertFromUtf32 rather than pasted literally -- an invisible/bidi character sitting
    // directly in this source file would make the file itself unreviewable.
    [Theory]
    [InlineData(0x202E)] // RIGHT-TO-LEFT OVERRIDE
    [InlineData(0x202D)] // LEFT-TO-RIGHT OVERRIDE
    [InlineData(0x2066)] // LEFT-TO-RIGHT ISOLATE
    [InlineData(0x200F)] // RIGHT-TO-LEFT MARK
    [InlineData(0x061C)] // ARABIC LETTER MARK
    [InlineData(0x2028)] // LINE SEPARATOR
    [InlineData(0x2029)] // PARAGRAPH SEPARATOR
    [InlineData(0x200B)] // ZERO WIDTH SPACE
    [InlineData(0xFEFF)] // ZERO WIDTH NO-BREAK SPACE / BOM
    [InlineData(0x180E)] // MONGOLIAN VOWEL SEPARATOR
    public async Task ReactAsync_RejectsEmbeddedBidiOrZeroWidthCharacters(int codepoint)
    {
        var content = "+" + char.ConvertFromUtf32(codepoint) + "x";

        await Assert.ThrowsAsync<ArgumentException>(() => _sut.ReactAsync(TextNote(), content));
        Assert.Empty(_backend.PublishedEvents);
    }

    // Negative control for the fix above: ZWJ/ZWNJ must stay allowed. Reactions are exactly where real
    // emoji (ZWJ sequences) and Indic/Arabic orthography (ZWNJ) show up, so blocking them would be the
    // over-blocking failure mode this fix is not supposed to introduce.
    [Fact]
    public async Task ReactAsync_StillAllowsZwjFamilyEmoji()
    {
        var zwj = char.ConvertFromUtf32(0x200D);
        var zwjFamily = char.ConvertFromUtf32(0x1F468) + zwj + char.ConvertFromUtf32(0x1F469) + zwj + char.ConvertFromUtf32(0x1F467);

        await _sut.ReactAsync(TextNote(), zwjFamily);

        Assert.Equal(zwjFamily, _backend.PublishedEvents.Single().Content);
    }

    [Fact]
    public async Task ReactAsync_StillAllowsZwnjArabicText()
    {
        var zwnjArabic = char.ConvertFromUtf32(0x0646) + char.ConvertFromUtf32(0x200C) + char.ConvertFromUtf32(0x0627);

        await _sut.ReactAsync(TextNote(), zwnjArabic);

        Assert.Equal(zwnjArabic, _backend.PublishedEvents.Single().Content);
    }

    [Fact]
    public async Task ReactAsync_RejectsNullTarget()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _sut.ReactAsync(null!));
    }

    [Fact]
    public async Task ReactAsync_WhenZeroRelaysAccept_PublishExceptionPropagatesUnswallowed()
    {
        var rejection = new EventPublishException(new PublishResult([]));
        _backend.RejectWith = rejection;

        var thrown = await Assert.ThrowsAsync<EventPublishException>(() => _sut.ReactAsync(TextNote()));
        Assert.Same(rejection, thrown);
    }
}
