using NightlyDawn.App.Composing;
using NightlyDawn.Core;
// NoteRow.AuthorLabel (the record property below) shadows the simple name "AuthorLabel", so the Core
// sanitizer needs an alias to stay callable from this file.
using CoreAuthorLabel = NightlyDawn.Core.AuthorLabel;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// What one row of a timeline column shows. Built from a <see cref="Note"/> the backend has already
/// verified (B13), but the content is still relay-supplied text: it is rendered through a plain
/// <c>TextBlock</c> (no markup interpretation) and truncated to <see cref="MaxContentChars"/> so a
/// multi-megabyte note cannot stall layout.
///
/// <para><see cref="AuthorLabel"/> is computed once, at construction, from whatever profile the caller had
/// resolved by then (plan §8.1: a record stays immutable rather than growing <c>INotifyPropertyChanged</c>
/// for this). <b>Known v1 limitation:</b> if a profile for this author arrives later, already-built rows do
/// not pick it up retroactively -- only rows built after the profile is cached show the resolved name.</para>
///
/// <para><see cref="Actions"/> is the same "decided once, at construction" treatment applied to publishing
/// (plan §10.1-1/§10.1-2): a row never grows a mutable "reaction sent" flag, so a reaction's result lands on
/// the column's <c>Status</c>, not here. Null for a row built without a publisher wired (designer preview,
/// sample factory, most unit tests) -- <see cref="CanReplyOrRepost"/> still reflects <see cref="Kind"/>
/// regardless, so a bound button's <c>IsEnabled</c> does not depend on whether a publisher exists.</para>
/// </summary>
public sealed record NoteRow(string Id, string AuthorPubkey, long CreatedAt, string DisplayContent, string TimeLabel, string AuthorLabel, NoteKind Kind, NoteRowActions? Actions = null)
{
    public const int MaxContentChars = 1_000;
    private const long MaxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z, DateTimeOffset's ceiling.

    /// <summary>Reply/Repost are disabled for anything that is not a plain kind:1 (plan §10.1-6) -- the
    /// publisher throws <see cref="ArgumentException"/> for the same reason (<c>NoteEventBuilder.Reply</c>/
    /// <c>Repost</c>); this is the UI half of that double guard, kept here (not on <see cref="Actions"/>) so
    /// it holds even when <see cref="Actions"/> is null.</summary>
    public bool CanReplyOrRepost => Kind == NoteKind.Text;

    /// <summary>
    /// <paramref name="profile"/> should be whatever the caller's <c>IProfileStore.TryGet(note.AuthorPubkey)</c>
    /// returned just before calling this -- null is a valid answer (unresolved or no kind:0 exists) and
    /// produces the pubkey-prefix fallback label via <see cref="CoreAuthorLabel.For"/>.
    /// </summary>
    public static NoteRow From(Note note, Profile? profile = null, DateTimeOffset? now = null, NoteRowActions? actions = null)
    {
        var content = note.Content;
        if (content.Length == 0 && note.Kind != NoteKind.Text)
        {
            content = note.Kind == NoteKind.Repost ? "(repost)" : "(generic repost)";
        }
        else if (content.Length > MaxContentChars)
        {
            content = string.Concat(content.AsSpan(0, MaxContentChars), "…");
        }

        return new NoteRow(
            note.Id,
            note.AuthorPubkey,
            note.CreatedAt,
            content,
            FormatTime(note.CreatedAt, now ?? DateTimeOffset.Now),
            CoreAuthorLabel.For(profile, note.AuthorPubkey),
            note.Kind,
            actions);
    }

    /// <summary>Local time for today's notes, date + time otherwise. A created_at outside DateTimeOffset's range (relays can send anything) renders as "?" instead of throwing.</summary>
    internal static string FormatTime(long createdAt, DateTimeOffset now)
    {
        if (createdAt < 0 || createdAt > MaxUnixSeconds)
        {
            return "?";
        }

        var local = DateTimeOffset.FromUnixTimeSeconds(createdAt).ToLocalTime();
        var today = now.ToLocalTime().Date;
        return local.Date == today ? local.ToString("HH:mm") : local.ToString("yyyy-MM-dd HH:mm");
    }
}
