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
    /// <summary>Named cap on a posted/replied/quoted body (plan §9.3), measured in UTF-8 <em>bytes</em>
    /// (<see cref="Encoding.UTF8.GetByteCount(string)"/>) — not <c>string.Length</c> (UTF-16 chars). A
    /// char-based cap lets the effective limit vary with the writer's language: with
    /// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> (see <c>NostrJson</c>), non-ASCII text rides the
    /// wire as raw UTF-8, so 65,536 CJK characters is 196,608 bytes — three times an equal-length ASCII body.
    /// Our own relays run Haven, whose <c>max-event-size</c> default is 131,072 bytes for kind:1/6/7 (the
    /// long-text exemption only covers kind 30023/30024/30040/30041), so that CJK body would be signed,
    /// broadcast, and rejected by every relay with <c>content is too large</c> — an
    /// <see cref="EventPublishException"/> thrown <em>after</em> the sign step, for a body that looked
    /// well under the old 65,536-char cap. This value is <c>131,072</c> minus a 4,096-byte envelope reserve:
    /// id/pubkey/sig/created_at/kind/content's own JSON punctuation is ~342 bytes with no tags, and the
    /// richest tag shape this publisher builds — <see cref="NoteEventBuilder.Reply"/> with a root+reply
    /// e-tag pair and ten mentioned-author p-tags — measures ~1,353 bytes; 4,096 leaves roughly 3x that
    /// headroom. Exceeding it throws rather than truncates — silently changing what the user typed, or
    /// cutting a UTF-8 sequence mid-rune, is worse than refusing to send it.</summary>
    public const int MaxContentBytes = 131_072 - 4_096;

    /// <summary>Named cap on <see cref="ReactAsync"/>'s <c>content</c> (plan §9.3). NIP-25 reactions are
    /// conventionally <c>"+"</c>, <c>"-"</c>, a single emoji, or a <c>:shortcode:</c> — nowhere near this long.
    /// This is the one place a UI can hand this type an arbitrary string, so the cap is deliberately tight.</summary>
    public const int MaxReactionContentLength = 32;

    public async Task<PublishedNote> PostNoteAsync(string content, CancellationToken cancellationToken = default)
    {
        ValidateContent(content);
        var unsigned = NoteEventBuilder.Post(content, Now());
        return await SignMapPublishAsync(unsigned, cancellationToken).ConfigureAwait(false);
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

    public async Task<PublishedNote> RepostAsync(Note target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var unsigned = NoteEventBuilder.Repost(target, Now());
        return await SignMapPublishAsync(unsigned, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PublishedNote> QuoteAsync(Note quoted, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quoted);
        ValidateContent(content);
        var unsigned = NoteEventBuilder.Quote(quoted, content, Now());
        return await SignMapPublishAsync(unsigned, cancellationToken).ConfigureAwait(false);
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

        var byteCount = Encoding.UTF8.GetByteCount(content);
        if (byteCount > MaxContentBytes)
        {
            throw new ArgumentException($"Content is {byteCount} UTF-8 bytes, exceeding the {MaxContentBytes}-byte limit.", nameof(content));
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
            if (IsDisallowedReactionRune(rune))
            {
                throw new ArgumentException("Reaction content must not contain control, bidirectional-override, or zero-width padding characters.", nameof(content));
            }
        }
    }

    /// <summary>Plan §9.5 blocking ②. A reaction's <c>content</c> is a collation key other clients group by
    /// (NIP-25): a bidirectional-override character makes <em>this</em> client's label render reversed on
    /// someone else's screen, and a zero-width padding character makes an invisible-to-us string fail to
    /// collate with a plain <c>"+"</c> even though the two look identical. <see cref="Rune.IsControl"/> only
    /// covers C0/C1 controls, so U+2028/U+2029 (which <see cref="string.IsNullOrWhiteSpace(string)"/> only
    /// catches when they are the <em>entire</em> string, not when embedded) and every Unicode Cf bidi/format
    /// character need an explicit check. U+200C (ZWNJ) and U+200D (ZWJ) are deliberately <b>not</b> in this
    /// list — reactions are exactly where real emoji (ZWJ sequences) and Indic/Arabic orthography (ZWNJ)
    /// arrive, and blocking them is the over-blocking failure mode, not the one this check is for.</summary>
    private static bool IsDisallowedReactionRune(Rune rune)
    {
        if (Rune.IsControl(rune))
        {
            return true;
        }

        return rune.Value switch
        {
            0x061C or 0x200E or 0x200F => true, // Arabic Letter Mark, LRM, RLM
            >= 0x202A and <= 0x202E => true, // LRE, RLE, PDF, LRO, RLO
            >= 0x2066 and <= 0x2069 => true, // LRI, RLI, FSI, PDI
            0x2028 or 0x2029 => true, // LINE SEPARATOR, PARAGRAPH SEPARATOR
            0x200B => true, // ZERO WIDTH SPACE
            >= 0x2060 and <= 0x2064 => true, // WORD JOINER + invisible math operators
            0xFEFF => true, // ZERO WIDTH NO-BREAK SPACE / BOM
            >= 0xFFF9 and <= 0xFFFB => true, // interlinear annotation anchor/separator/terminator
            0x180E => true, // MONGOLIAN VOWEL SEPARATOR
            _ => false,
        };
    }
}
