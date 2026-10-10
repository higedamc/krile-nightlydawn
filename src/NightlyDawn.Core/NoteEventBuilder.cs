namespace NightlyDawn.Core;

/// <summary>
/// Pure event construction for the typed posting actions behind <see cref="INotePublisher"/> (plan §1.5): no
/// I/O, no signer, no relay. This is the one piece of the posting contract 0d implements and tests (the rest
/// is declaration-only) precisely because it is pure: "a reply always carries e/p tags" becomes a fact this
/// class alone can prove, with no relay and no signer anywhere in the test.
///
/// <para><b>Tag spec (exact, so it is checkable against this text, not just against the code):</b></para>
/// <list type="bullet">
/// <item><description><b><see cref="Reply"/></b>: if <c>parent</c> is itself a reply
/// (<see cref="Note.RootId"/> is set), the result carries both a root e-tag -- unchanged, still pointing at
/// the thread's original root -- and a reply e-tag pointing at <c>parent</c> (NIP-10). If <c>parent</c> is
/// top-level (no existing root), the result carries only a reply e-tag pointing at <c>parent</c>: there is
/// nothing else to call "root" yet. p-tags are <c>parent</c>'s author plus its
/// <see cref="Note.MentionedPubkeys"/>, deduplicated, with the replying author always excluded (never mention
/// yourself). Every e-tag's relay hint is <see cref="Note.FirstSeenOnRelay"/> if known, else the empty string
/// (NIP-10 permits an empty hint). The root e-tag's own pubkey hint (the NIP-10 marker's optional 5th
/// element) is intentionally omitted: a <see cref="Note"/> only carries its immediate parent's author, never
/// the root's, so there is nothing honest to put there without a second lookup this method does not have.
/// Throws <see cref="ArgumentException"/> when <c>parent</c> is not <see cref="NoteKind.Text"/> -- you cannot
/// reply to a repost wrapper, only to the note it wraps.</description></item>
/// <item><description><b><see cref="Repost"/></b> (NIP-18): kind 6, one e-tag at the target plus a p-tag for
/// its author, empty content. Throws <see cref="ArgumentException"/> when <c>target</c> is not
/// <see cref="NoteKind.Text"/> -- you cannot repost a repost.</description></item>
/// <item><description><b><see cref="Quote"/></b> (NIP-18): kind 1, a q-tag at the quoted note plus a p-tag
/// for its author (so the quoted author is notified, the same as a reply would notify them). v1 does not
/// parse <c>nostr:</c> URIs out of free text into tags (NIP-27) -- the q-tag this method adds is the only tag
/// a quote carries; any <c>nostr:</c> reference the caller writes into <c>content</c> is left as plain
/// text.</description></item>
/// <item><description><b><see cref="Reaction"/></b> (NIP-25): kind 7, e/p tags at the target plus a k-tag
/// naming the target's numeric kind, content <c>"+"</c> by default.</description></item>
/// </list>
///
/// <para>None of these take a <see cref="TimeProvider"/>: <c>createdAt</c> is a plain parameter so these
/// functions stay deterministic in a test without needing one. <see cref="INotePublisher"/>'s implementation
/// is where a <see cref="TimeProvider"/> belongs.</para>
/// </summary>
public static class NoteEventBuilder
{
    public static UnsignedNostrEvent Reply(Note parent, string content, string authorPubkey, long createdAt)
    {
        ArgumentNullException.ThrowIfNull(parent);
        RequireTextKind(parent, "reply to", nameof(parent));

        var hint = RelayHint(parent);
        List<IReadOnlyList<string>> tags = [];

        if (parent.RootId is { } rootId)
        {
            tags.Add(["e", rootId, hint, "root"]);
            tags.Add(["e", parent.Id, hint, "reply", parent.AuthorPubkey]);
        }
        else
        {
            tags.Add(["e", parent.Id, hint, "reply", parent.AuthorPubkey]);
        }

        foreach (var p in ReplyMentions(parent, authorPubkey))
        {
            tags.Add(["p", p]);
        }

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Text, Tags: tags, Content: content);
    }

    public static UnsignedNostrEvent Repost(Note target, string authorPubkey, long createdAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireTextKind(target, "repost", nameof(target));

        var hint = RelayHint(target);
        List<IReadOnlyList<string>> tags = [["e", target.Id, hint], ["p", target.AuthorPubkey]];

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Repost, Tags: tags, Content: string.Empty);
    }

    public static UnsignedNostrEvent Quote(Note quoted, string content, string authorPubkey, long createdAt)
    {
        ArgumentNullException.ThrowIfNull(quoted);

        var hint = RelayHint(quoted);
        List<IReadOnlyList<string>> tags = [["q", quoted.Id, hint], ["p", quoted.AuthorPubkey]];

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Text, Tags: tags, Content: content);
    }

    public static UnsignedNostrEvent Reaction(Note target, string authorPubkey, long createdAt, string content = "+")
    {
        ArgumentNullException.ThrowIfNull(target);

        var hint = RelayHint(target);
        List<IReadOnlyList<string>> tags =
        [
            ["e", target.Id, hint],
            ["p", target.AuthorPubkey],
            ["k", ((int)target.Kind).ToString()],
        ];

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: 7, Tags: tags, Content: content);
    }

    private static void RequireTextKind(Note note, string action, string paramName)
    {
        if (note.Kind != NoteKind.Text)
        {
            throw new ArgumentException($"Cannot {action} a {note.Kind} note; only {nameof(NoteKind.Text)} notes can be targeted this way.", paramName);
        }
    }

    private static string RelayHint(Note note) => note.FirstSeenOnRelay?.Value ?? string.Empty;

    /// <summary>parent's author, then parent's own mentions, in that order, deduplicated, with the replying
    /// author (never mention yourself) removed.</summary>
    private static IReadOnlyList<string> ReplyMentions(Note parent, string authorPubkey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        List<string> result = [];

        void AddIfNew(string pubkey)
        {
            if (!string.Equals(pubkey, authorPubkey, StringComparison.Ordinal) && seen.Add(pubkey))
            {
                result.Add(pubkey);
            }
        }

        AddIfNew(parent.AuthorPubkey);
        foreach (var mentioned in parent.MentionedPubkeys ?? [])
        {
            AddIfNew(mentioned);
        }

        return result;
    }
}
