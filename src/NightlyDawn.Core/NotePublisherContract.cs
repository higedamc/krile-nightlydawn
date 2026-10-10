namespace NightlyDawn.Core;

/// <summary>Return value of every <see cref="INotePublisher"/> method: callers need the <see cref="Note"/>
/// immediately to insert it into a column optimistically -- nothing would appear until a relay echoed the
/// event back, otherwise -- not just the per-relay <see cref="Core.PublishResult"/> (B5: zero-of-N accepted is
/// a failure; see <see cref="EventPublishException"/>).</summary>
public sealed record PublishedNote(Note Note, PublishResult PublishResult);

/// <summary>
/// Typed posting actions, one method per action (plan §1.5, Lead's call) instead of a generic
/// <c>PublishAsync(kind, tags)</c>: a generic signature would let the App construct a reply with no e/p tags,
/// exactly the mistake a typed method makes impossible to write. Every method here builds its event via
/// <see cref="NoteEventBuilder"/> (pure, already implemented and tested in 0d), signs it with
/// <see cref="IKeyStore.SignEventAsync"/>, and publishes it with <see cref="INostrBackend.PublishAsync"/>.
///
/// <para><b>Declaration only (0d leaf brief): no implementation here.</b> Implementation is L3, after 0e's
/// view extraction gives a compose box somewhere to mount that is not <c>MainWindow.axaml</c>.</para>
///
/// <para><b><c>created_at</c></b>: implementations must inject a <see cref="TimeProvider"/> rather than
/// reading <see cref="DateTimeOffset.UtcNow"/> directly, so tests that exercise a full publisher (not just the
/// <see cref="NoteEventBuilder"/> functions it calls) stay deterministic.</para>
///
/// <para><b>Publish target</b>: <see cref="INostrBackend.PublishAsync"/> sends to the whole connected relay
/// pool (today, the read list from <c>NIGHTLYDAWN_RELAYS</c>), not <see cref="Account"/>'s
/// <see cref="RelayListEntry.WriteRelays"/> (NIP-65 write routing). That is acceptable for Phase 2 -- an
/// implementation must not silently narrow to a relay subset without this doc comment being updated first,
/// since that would be a behavior change nothing else documents.</para>
///
/// <para><b>Deletion</b> (NIP-09, kind:5) is out of scope for v1: there is deliberately no
/// <c>DeleteAsync</c> on this interface.</para>
/// </summary>
public interface INotePublisher
{
    /// <summary>Posts a new top-level kind:1 note.</summary>
    Task<PublishedNote> PostNoteAsync(string content, CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentException"><paramref name="parent"/> is not <see cref="NoteKind.Text"/> -- see <see cref="NoteEventBuilder.Reply"/>.</exception>
    Task<PublishedNote> ReplyToAsync(Note parent, string content, CancellationToken cancellationToken = default);

    /// <exception cref="ArgumentException"><paramref name="target"/> is not <see cref="NoteKind.Text"/> -- see <see cref="NoteEventBuilder.Repost"/>.</exception>
    Task<PublishedNote> RepostAsync(Note target, CancellationToken cancellationToken = default);

    Task<PublishedNote> QuoteAsync(Note quoted, string content, CancellationToken cancellationToken = default);

    /// <param name="target">The note being reacted to.</param>
    /// <param name="content">NIP-25 reaction content; <c>"+"</c> (the conventional like) unless the caller passes something else (an emoji reaction).</param>
    Task<PublishedNote> ReactAsync(Note target, string content = "+", CancellationToken cancellationToken = default);
}
