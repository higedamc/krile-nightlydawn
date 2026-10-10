using NightlyDawn.Core;

namespace NightlyDawn.Filters.Tests;

internal static class TestNotes
{
    public const string AuthorHex = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static Note Make(
        NoteKind kind = NoteKind.Text,
        string content = "hello",
        long createdAt = 1_700_000_000,
        string authorPubkey = AuthorHex,
        string? replyId = null,
        string? rootId = null,
        IReadOnlyList<string>? hashtags = null,
        RelayUrl? firstSeenOnRelay = null) =>
        new(
            Id: "0000000000000000000000000000000000000000000000000000000000000000"[..64],
            AuthorPubkey: authorPubkey,
            CreatedAt: createdAt,
            Kind: kind,
            Content: content,
            Tags: [],
            RootId: rootId,
            ReplyId: replyId,
            Hashtags: hashtags,
            FirstSeenOnRelay: firstSeenOnRelay);
}
