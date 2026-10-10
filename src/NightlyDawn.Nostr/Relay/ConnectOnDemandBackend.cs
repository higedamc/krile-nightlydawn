using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Relay;

/// <summary>
/// An <see cref="INostrBackend"/> that connects the relay pool before it publishes (L3c). Everything else is
/// pass-through.
///
/// <para>Why this exists: <see cref="NostrBackend.PublishAsync"/> throws
/// <see cref="RelayConnectionException"/> when no relay is connected, and
/// <c>NostrTimelineSourceFactory</c> connects lazily — on the first <em>timeline stream</em>. Publishing has no
/// such trigger of its own, so a post sent before the first column finished connecting would fail with a
/// connectivity error for a pool that was simply not dialled yet. The UI turns that into a status line naming
/// an exception type (L3b), which is honest but useless to the writer, and the text is gone either way.</para>
///
/// <para>Only <see cref="PublishAsync"/> is intercepted. The read paths already have their own connect step
/// (<c>NostrTimelineSource</c> is handed the same callback), so wrapping them here would double it; a future
/// read path that needs it should take the callback the same way rather than relying on this type.</para>
///
/// <para>Interception sits at the backend rather than at <see cref="INotePublisher"/> on purpose: there is one
/// place a note reaches a relay, and a sixth publish action added to <see cref="INotePublisher"/> later cannot
/// forget to go through it.</para>
/// </summary>
internal sealed class ConnectOnDemandBackend(INostrBackend inner, Func<CancellationToken, Task> ensureConnected) : INostrBackend
{
    private readonly INostrBackend _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<CancellationToken, Task> _ensureConnected = ensureConnected ?? throw new ArgumentNullException(nameof(ensureConnected));

    /// <summary>Connects first, then publishes. A failed connect propagates as-is — it is the truthful error for
    /// "we never reached a relay", and it is the same exception type the pool itself would have thrown.</summary>
    public async Task<PublishResult> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default)
    {
        await _ensureConnected(cancellationToken).ConfigureAwait(false);
        return await _inner.PublishAsync(signedEvent, cancellationToken).ConfigureAwait(false);
    }

    public Task ConnectAsync(IReadOnlyCollection<RelayUrl> relayUrls, CancellationToken cancellationToken = default) =>
        _inner.ConnectAsync(relayUrls, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        _inner.DisconnectAsync(cancellationToken);

    public IAsyncEnumerable<SubscriptionMessage> SubscribeAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
        _inner.SubscribeAsync(filter, cancellationToken);

    public Task<IReadOnlyList<NostrEvent>> FetchAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
        _inner.FetchAsync(filter, cancellationToken);

    public Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default) =>
        _inner.FetchProfileEventAsync(pubkey, cancellationToken);

    public Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default) =>
        _inner.FetchRelayListAsync(pubkey, cancellationToken);
}
