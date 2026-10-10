namespace NightlyDawn.Core;

/// <summary>
/// Pure event construction for the typed posting actions behind <see cref="INotePublisher"/> (plan §1.5): no
/// I/O, no signer, no relay. This is the one piece of the posting contract 0d implements and tests (the rest
/// is declaration-only) precisely because it is pure: "a reply always carries e/p tags" becomes a fact this
/// class alone can prove, with no relay and no signer anywhere in the test.
///
/// <para><b>Tag spec (exact, so it is checkable against this text, not just against the code):</b></para>
/// <list type="bullet">
/// <item><description><b><see cref="Reply"/></b> (NIP-10, marked e-tags): a direct reply to a top-level note
/// carries a single e-tag marked <c>"root"</c> -- the parent IS the root, so there is nothing else to call
/// it ("For top level replies ... only the 'root' marker should be used", NIP-10). A reply to a note that is
/// itself a reply (<c>parent</c> has <see cref="Note.RootId"/> set) carries two e-tags: <c>"root"</c>
/// unchanged, still pointing at the thread's original root, and <c>"reply"</c> pointing at
/// <c>parent</c>. p-tags are <c>parent</c>'s author plus its <see cref="Note.MentionedPubkeys"/>,
/// deduplicated, with the replying author always excluded (never mention yourself). Every e-tag's relay hint
/// is <see cref="Note.FirstSeenOnRelay"/> if known, else the empty string (NIP-10 permits an empty hint);
/// when the tag marked <c>"root"</c> points at an ancestor further back than <c>parent</c> (the
/// two-e-tag case), this is only a guess at where the root itself can be found -- a <see cref="Note"/> does
/// not carry its root ancestor's own <see cref="Note.FirstSeenOnRelay"/>, only its immediate parent's.
/// Throws <see cref="ArgumentException"/> when <c>parent</c> is not <see cref="NoteKind.Text"/> -- you cannot
/// reply to a repost wrapper, only to the note it wraps.</description></item>
/// <item><description><b><see cref="Repost"/></b> (NIP-18): kind 6, one e-tag at the target plus a p-tag for
/// its author, empty content. Throws <see cref="ArgumentException"/> when <c>target</c> is not
/// <see cref="NoteKind.Text"/> -- you cannot repost a repost.</description></item>
/// <item><description><b><see cref="Quote"/></b> (NIP-18): kind 1, a q-tag
/// (<c>["q", id, relayHint, authorPubkey]</c>, all four elements per the NIP-18 syntax) at the quoted note
/// plus a p-tag for its author (so the quoted author is notified, the same as a reply would notify them). v1
/// does not parse <c>nostr:</c> URIs out of free text into tags (NIP-27) -- the q-tag this method adds is the
/// only tag a quote carries; any <c>nostr:</c> reference the caller writes into <c>content</c> is left as
/// plain text.</description></item>
/// <item><description><b><see cref="Reaction"/></b> (NIP-25): kind 7, e/p tags at the target plus a k-tag
/// naming the target's numeric kind, content <c>"+"</c> by default.</description></item>
/// <item><description><b><see cref="Post"/></b>: kind 1, no tags. The only one of these five actions with no
/// tag rule to get wrong, included anyway so every <see cref="INotePublisher"/> action has a matching pure
/// function to test and to call.</description></item>
/// </list>
///
/// <para>None of these take a <see cref="TimeProvider"/>: <c>createdAt</c> is a plain parameter so these
/// functions stay deterministic in a test without needing one. <see cref="INotePublisher"/>'s implementation
/// is where a <see cref="TimeProvider"/> belongs.</para>
/// </summary>
public static class NoteEventBuilder
{
    public static UnsignedNostrEvent Post(string content, long createdAt) =>
        new(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Text, Tags: [], Content: content);

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
            // parent IS the root: NIP-10 says a direct reply to a top-level note gets a single
            // "root"-marked e-tag, not "reply" -- there is nothing else to call the root here.
            tags.Add(["e", parent.Id, hint, "root", parent.AuthorPubkey]);
        }

        foreach (var p in ReplyMentions(parent, authorPubkey))
        {
            tags.Add(["p", p]);
        }

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Text, Tags: tags, Content: content);
    }

    public static UnsignedNostrEvent Repost(Note target, long createdAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireTextKind(target, "repost", nameof(target));

        var hint = RelayHint(target);
        List<IReadOnlyList<string>> tags = [["e", target.Id, hint], ["p", target.AuthorPubkey]];

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Repost, Tags: tags, Content: string.Empty);
    }

    public static UnsignedNostrEvent Quote(Note quoted, string content, long createdAt)
    {
        ArgumentNullException.ThrowIfNull(quoted);

        var hint = RelayHint(quoted);
        List<IReadOnlyList<string>> tags = [["q", quoted.Id, hint, quoted.AuthorPubkey], ["p", quoted.AuthorPubkey]];

        return new UnsignedNostrEvent(Pubkey: null, CreatedAt: createdAt, Kind: (int)NoteKind.Text, Tags: tags, Content: content);
    }

    public static UnsignedNostrEvent Reaction(Note target, long createdAt, string content = "+")
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
