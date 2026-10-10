using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>
/// In-process <see cref="IEventMapper"/> for publisher tests (L3a). <see cref="ToNote"/> defaults to a
/// faithful mapping (enough fields to assert tag/kind wiring); <see cref="ThrowOnToNote"/> lets a test force the
/// map step to fail, to prove <see cref="NotePublisher"/> never publishes an event it could not map (plan §9.2-1).
/// </summary>
internal sealed class FakeEventMapper : IEventMapper
{
    public Exception? ThrowOnToNote { get; set; }

    public int ToNoteCallCount { get; private set; }

    public Note ToNote(NostrEvent nostrEvent)
    {
        ToNoteCallCount++;
        if (ThrowOnToNote is { } exception)
        {
            throw exception;
        }

        return new Note(
            nostrEvent.Id,
            nostrEvent.Pubkey,
            nostrEvent.CreatedAt,
            (NoteKind)nostrEvent.Kind,
            nostrEvent.Content,
            nostrEvent.Tags);
    }

    public Profile ToProfile(NostrEvent nostrEvent) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public RelayListEntry ToRelayList(NostrEvent nostrEvent) =>
        throw new NotSupportedException("NotePublisher never calls this.");
}
