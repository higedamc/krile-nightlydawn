using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>
/// In-process <see cref="INostrBackend"/> for publisher tests (L3a): no sockets, no relay pool. Only
/// <see cref="PublishAsync"/> is wired up -- <see cref="NotePublisher"/> never calls the other members, and a
/// fake that stubbed them would just be dead code pretending to be coverage.
/// </summary>
internal sealed class FakeNostrBackend : INostrBackend
{
    public List<NostrEvent> PublishedEvents { get; } = [];

    /// <summary>When set, <see cref="PublishAsync"/> throws this instead of recording/accepting -- mirrors the
    /// real <see cref="Relay.NostrBackend"/>'s B5 contract (0-of-N accepted throws <see cref="EventPublishException"/>
    /// rather than returning a result the caller could mistake for success).</summary>
    public EventPublishException? RejectWith { get; set; }

    public Task<PublishResult> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default)
    {
        if (RejectWith is { } exception)
        {
            return Task.FromException<PublishResult>(exception);
        }

        PublishedEvents.Add(signedEvent);
        var outcome = new RelayPublishOutcome(RelayUrl.Parse("wss://fake.example/"), Accepted: true);
        return Task.FromResult(new PublishResult([outcome]));
    }

    public Task ConnectAsync(IReadOnlyCollection<RelayUrl> relayUrls, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public IAsyncEnumerable<SubscriptionMessage> SubscribeAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<IReadOnlyList<NostrEvent>> FetchAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");

    public Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("NotePublisher never calls this.");
}
