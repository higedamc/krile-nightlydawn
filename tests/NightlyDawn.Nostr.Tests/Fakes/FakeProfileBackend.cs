using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>
/// Minimal <see cref="INostrBackend"/> double for <c>ProfileStoreTests</c>: only <see cref="FetchAsync"/> is
/// implemented (that is the only method <see cref="Nostr.Profiles.ProfileStore"/> calls); every other member
/// throws if a future change ever starts using it, so the test would fail loudly instead of silently no-op'ing.
/// </summary>
internal sealed class FakeProfileBackend : INostrBackend
{
    private readonly Queue<IReadOnlyList<NostrEvent>> _scriptedResults = new();

    public List<NostrFilter> FetchCalls { get; } = [];

    public FakeProfileBackend Enqueue(IReadOnlyList<NostrEvent> events)
    {
        _scriptedResults.Enqueue(events);
        return this;
    }

    public Task<IReadOnlyList<NostrEvent>> FetchAsync(NostrFilter filter, CancellationToken cancellationToken = default)
    {
        FetchCalls.Add(filter);
        var result = _scriptedResults.Count > 0 ? _scriptedResults.Dequeue() : [];
        return Task.FromResult(result);
    }

    public Task ConnectAsync(IReadOnlyCollection<RelayUrl> relayUrls, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<SubscriptionMessage> SubscribeAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PublishResult> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(); // The point of ProfileStore is to never call this per-author endpoint.

    public Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
