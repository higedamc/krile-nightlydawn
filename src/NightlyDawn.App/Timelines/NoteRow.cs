using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// What one row of a timeline column shows. Built from a <see cref="Note"/> the backend has already
/// verified (B13), but the content is still relay-supplied text: it is rendered through a plain
/// <c>TextBlock</c> (no markup interpretation) and truncated to <see cref="MaxContentChars"/> so a
/// multi-megabyte note cannot stall layout.
/// </summary>
public sealed record NoteRow(string Id, string AuthorPubkey, long CreatedAt, string DisplayContent, string TimeLabel)
{
    public const int MaxContentChars = 1_000;
    private const int AuthorPrefixChars = 8;
    private const long MaxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z, DateTimeOffset's ceiling.

    /// <summary>First eight hex characters of the author pubkey. npub rendering arrives with 1b/1f; hex is unambiguous meanwhile.</summary>
    public string AuthorLabel => AuthorPubkey.Length > AuthorPrefixChars
        ? string.Concat(AuthorPubkey.AsSpan(0, AuthorPrefixChars), "…")
        : AuthorPubkey;

    public static NoteRow From(Note note, DateTimeOffset? now = null)
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

        return new NoteRow(note.Id, note.AuthorPubkey, note.CreatedAt, content, FormatTime(note.CreatedAt, now ?? DateTimeOffset.Now));
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
