using System.Text;
using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Publishing;

/// <summary>
/// <see cref="INotePublisher"/> over <see cref="INostrBackend"/> + <see cref="IKeyStore"/> (plan §9, L3a).
/// Every action follows the same fixed order (plan §9.2-1): build the unsigned event via
/// <see cref="NoteEventBuilder"/> (pure) → sign it (<see cref="IKeyStore.SignEventAsync"/>) → map the signed
/// event onto a <see cref="Note"/> (<see cref="IEventMapper.ToNote"/>) → publish
/// (<see cref="INostrBackend.PublishAsync"/>). Mapping happens <em>before</em> publishing specifically so a
/// mapping bug (which would be this project's bug, not a relay's) can never leave the event published with
/// nothing returned to the caller.
///
/// <para>Never logs <see cref="Note.Content"/> or any pubkey (plan §9.3): this class has no logger dependency
/// at all, so there is nothing to forget to redact.</para>
///
/// <para>Does not sanitize content — see each method's validation: reject or pass through, never rewrite what
/// the user typed.</para>
/// </summary>
public sealed class NotePublisher(
    INostrBackend backend,
    IKeyStore keyStore,
    IEventMapper mapper,
    TimeProvider timeProvider) : INotePublisher
{
    /// <summary>Named cap on a posted/replied/quoted body (plan §9.3): large enough for any real note, far
    /// short of the "sign and broadcast a 10 MB paste to every relay" failure mode the cap exists to prevent.
    /// Exceeding it throws rather than truncates — silently changing what the user typed is worse than
    /// refusing to send it.</summary>
    public const int MaxContentLength = 65_536;

    /// <summary>Named cap on <see cref="ReactAsync"/>'s <c>content</c> (plan §9.3). NIP-25 reactions are
    /// conventionally <c>"+"</c>, <c>"-"</c>, a single emoji, or a <c>:shortcode:</c> — nowhere near this long.
    /// This is the one place a UI can hand this type an arbitrary string, so the cap is deliberately tight.</summary>
    public const int MaxReactionContentLength = 32;

    public Task<PublishedNote> PostNoteAsync(string content, CancellationToken cancellationToken = default)
    {
        ValidateContent(content);
        var unsigned = NoteEventBuilder.Post(content, Now());
        return SignMapPublishAsync(unsigned, cancellationToken);
    }

    public async Task<PublishedNote> ReplyToAsync(Note parent, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ValidateContent(content);

        // The replying author's own pubkey must be excluded from the p-tags NoteEventBuilder derives from
        // parent (never mention yourself) -- fetched fresh, not cached, since the active signer can change
        // between calls (plan §9.2-3).
        var signer = await keyStore.GetActiveSignerAsync(cancellationToken).ConfigureAwait(false);
        var unsigned = NoteEventBuilder.Reply(parent, content, signer.Pubkey, Now());
        return await SignMapPublishAsync(unsigned, cancellationToken).ConfigureAwait(false);
    }

    public Task<PublishedNote> RepostAsync(Note target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var unsigned = NoteEventBuilder.Repost(target, Now());
        return SignMapPublishAsync(unsigned, cancellationToken);
    }

    public Task<PublishedNote> QuoteAsync(Note quoted, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quoted);
        ValidateContent(content);
        var unsigned = NoteEventBuilder.Quote(quoted, content, Now());
        return SignMapPublishAsync(unsigned, cancellationToken);
    }

    /// <summary>Returns <see cref="PublishResult"/>, not <see cref="PublishedNote"/> (plan §9.1): a reaction is
    /// kind:7, which <see cref="NoteKind"/> cannot represent, and a reaction is never inserted as a timeline
    /// row (the UI only flips a heart's state), so there is no <see cref="Note"/> to return and nothing to
    /// map.</summary>
    public async Task<PublishResult> ReactAsync(Note target, string content = "+", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateReactionContent(content);
        var unsigned = NoteEventBuilder.Reaction(target, Now(), content);
        var signed = await keyStore.SignEventAsync(unsigned, cancellationToken).ConfigureAwait(false);
        return await backend.PublishAsync(signed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PublishedNote> SignMapPublishAsync(UnsignedNostrEvent unsigned, CancellationToken cancellationToken)
    {
        var signed = await keyStore.SignEventAsync(unsigned, cancellationToken).ConfigureAwait(false);
        var note = mapper.ToNote(signed); // map before publish -- see class doc (plan §9.2-1)
        var result = await backend.PublishAsync(signed, cancellationToken).ConfigureAwait(false);
        return new PublishedNote(note, result);
    }

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private static void ValidateContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Content must not be empty or whitespace-only.", nameof(content));
        }

        if (content.Length > MaxContentLength)
        {
            throw new ArgumentException($"Content exceeds the {MaxContentLength}-character limit.", nameof(content));
        }
    }

    private static void ValidateReactionContent(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ArgumentException("Reaction content must not be empty or whitespace-only.", nameof(content));
        }

        if (content.Length > MaxReactionContentLength)
        {
            throw new ArgumentException($"Reaction content exceeds the {MaxReactionContentLength}-character limit.", nameof(content));
        }

        foreach (var rune in content.EnumerateRunes())
        {
            if (Rune.IsControl(rune))
            {
                throw new ArgumentException("Reaction content must not contain control characters.", nameof(content));
            }
        }
    }
}
