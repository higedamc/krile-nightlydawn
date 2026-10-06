using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Mapping;
using NightlyDawn.Nostr.Transport;

namespace NightlyDawn.Nostr.Relay;

/// <summary>
/// <see cref="INostrBackend"/> over a pool of <see cref="RelayClient"/>s (leaf 1a).
/// Semantics decided in the 0c review:
/// B5 — <see cref="PublishAsync"/> returns a per-relay <see cref="PublishResult"/> and throws only when no relay accepted;
/// B6 — <see cref="SubscribeAsync"/> yields <see cref="EventReceived"/>/<see cref="EndOfStoredEvents"/> per relay and a single
/// <see cref="SubscriptionClosed"/> when the whole subscription ends; <see cref="FetchAsync"/> waits for EOSE from every
/// relay that was connected when the fetch started (never just the fastest one), bounded by <see cref="NostrBackendOptions.FetchTimeout"/>.
/// </summary>
public sealed class NostrBackend : INostrBackend, IAsyncDisposable
{
    private readonly IRelayConnectionFactory _connections;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, RelayClient> _relays = new(StringComparer.Ordinal);

    public NostrBackend(NostrBackendOptions? options = null, ILogger<NostrBackend>? logger = null)
        : this(new WebSocketRelayConnectionFactory(), options, logger)
    {
    }

    internal NostrBackend(IRelayConnectionFactory connections, NostrBackendOptions? options = null, ILogger? logger = null)
    {
        _connections = connections;
        Options = options ?? new NostrBackendOptions();
        _logger = logger ?? NullLogger.Instance;
        Mapper = new EventMapper(Diagnostics, _logger);
    }

    public NostrBackendOptions Options { get; }

    public NostrBackendDiagnostics Diagnostics { get; } = new();

    public IEventMapper Mapper { get; }

    public IReadOnlyCollection<RelayUrl> ConnectedRelays =>
        _relays.Values.Where(r => r.IsConnected).Select(r => r.Url).ToList();

    /// <summary>Adds relays to the pool and connects them in parallel. A relay that fails to connect stays in the pool and is retried by its reconnect loop; the call throws only if <em>no</em> relay could be connected.</summary>
    public async Task ConnectAsync(IReadOnlyCollection<RelayUrl> relayUrls, CancellationToken cancellationToken = default)
    {
        var clients = new List<RelayClient>();
        foreach (var url in relayUrls)
        {
            var client = _relays.GetOrAdd(url.Value, _ => new RelayClient(url, _connections, Options, Diagnostics, _logger));
            clients.Add(client);
        }

        var failures = new List<RelayConnectionException>();
        await Task.WhenAll(clients.Select(async client =>
        {
            try
            {
                await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (RelayConnectionException ex)
            {
                _logger.LogWarning("Relay {Relay}: initial connect failed, will retry in background", client.Url.Value);
                lock (failures)
                {
                    failures.Add(ex);
                }

                client.ScheduleReconnect();
            }
        })).ConfigureAwait(false);

        if (clients.Count > 0 && failures.Count == clients.Count)
        {
            throw new RelayConnectionException("(pool)", $"None of the {clients.Count} relays could be connected");
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var clients = _relays.Values.ToList();
        _relays.Clear();
        foreach (var client in clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<SubscriptionMessage> SubscribeAsync(NostrFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriptionId = NewSubscriptionId();
        var channel = Channel.CreateBounded<SubscriptionMessage>(
            new BoundedChannelOptions(Options.MaxBufferedMessagesPerSubscription)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            itemDropped: _ => Diagnostics.CountDroppedBufferedMessage());
        var relays = _relays.Values.ToList();
        var sink = new ChannelSink(channel.Writer, _logger, subscriptionId, relays.Select(r => r.Url.Value));
        foreach (var relay in relays)
        {
            await relay.SubscribeAsync(subscriptionId, filter, sink, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Subscription {Subscription} opened on {Count} relays (kinds: {Kinds})", subscriptionId, relays.Count, filter.Kinds is null ? "any" : string.Join(',', filter.Kinds));
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out var message))
                {
                    yield return message;
                }
            }
        }
        finally
        {
            foreach (var relay in relays)
            {
                try
                {
                    await relay.UnsubscribeAsync(subscriptionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (RelayConnectionException)
                {
                    // Relay already gone; nothing to close.
                }
            }

            _logger.LogDebug("Subscription {Subscription} closed", subscriptionId);
        }

        // Reached only when the token is not cancelled yet the channel completed (all relays done); report once.
        yield return new SubscriptionClosed("all relays finished");
    }

    public async Task<IReadOnlyList<NostrEvent>> FetchAsync(NostrFilter filter, CancellationToken cancellationToken = default)
    {
        var connected = _relays.Values.Where(r => r.IsConnected).ToList();
        if (connected.Count == 0)
        {
            throw new RelayConnectionException("(pool)", "No connected relays to fetch from");
        }

        var subscriptionId = NewSubscriptionId();
        var collector = new FetchCollector(connected.Select(r => r.Url.Value).ToHashSet(StringComparer.Ordinal), Options.MaxEventsPerFetch, Diagnostics);
        foreach (var relay in connected)
        {
            await relay.SubscribeAsync(subscriptionId, filter, collector, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Options.FetchTimeout);
            try
            {
                await collector.AllRelaysDone.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Fetch {Subscription}: {Pending} of {Total} relays did not send EOSE within {Timeout}s; returning what arrived", subscriptionId, collector.PendingCount, connected.Count, Options.FetchTimeout.TotalSeconds);
            }
        }
        finally
        {
            foreach (var relay in connected)
            {
                try
                {
                    await relay.UnsubscribeAsync(subscriptionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (RelayConnectionException)
                {
                    // Relay already gone.
                }
            }
        }

        return collector.Snapshot();
    }

    public async Task<PublishResult> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default)
    {
        var connected = _relays.Values.Where(r => r.IsConnected).ToList();
        var outcomes = await Task.WhenAll(connected.Select(r => r.PublishAsync(signedEvent, cancellationToken))).ConfigureAwait(false);
        var result = new PublishResult(outcomes);
        if (!result.AnyAccepted)
        {
            _logger.LogWarning("Publish of {EventId} kind {Kind}: accepted by 0 of {Total} relays", Prefix(signedEvent.Id), signedEvent.Kind, connected.Count);
            throw new EventPublishException(result);
        }

        _logger.LogInformation("Publish of {EventId} kind {Kind}: accepted by {Accepted} of {Total} relays", Prefix(signedEvent.Id), signedEvent.Kind, outcomes.Count(o => o.Accepted), connected.Count);
        return result;
    }

    public async Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default)
    {
        var events = await FetchAsync(new NostrFilter(Authors: [pubkey], Kinds: [0], Limit: 1), cancellationToken).ConfigureAwait(false);
        return events.Where(e => e.Pubkey == pubkey).MaxBy(e => e.CreatedAt);
    }

    public async Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default)
    {
        var events = await FetchAsync(new NostrFilter(Authors: [pubkey], Kinds: [10002], Limit: 1), cancellationToken).ConfigureAwait(false);
        var newest = events.Where(e => e.Pubkey == pubkey).MaxBy(e => e.CreatedAt);
        if (newest is null)
        {
            return null;
        }

        try
        {
            return Mapper.ToRelayList(newest);
        }
        catch (EventMappingException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private static string NewSubscriptionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private static string Prefix(string id) => id.Length >= 8 ? id[..8] : id;

    /// <summary>
    /// Live subscription sink. Forwards into the bounded DropOldest channel (drops are counted by the channel's
    /// itemDropped callback, so the relay receive loop never blocks on a slow consumer). The channel is completed
    /// — which ends the subscription with a single <see cref="SubscriptionClosed"/> — only when every relay has
    /// permanently closed the subscription; a dropped connection is transient (the relay re-arms the REQ on reconnect).
    /// </summary>
    private sealed class ChannelSink : ISubscriptionSink
    {
        private readonly ChannelWriter<SubscriptionMessage> _writer;
        private readonly ILogger _logger;
        private readonly string _subscriptionId;
        private readonly HashSet<string> _openRelays;
        private readonly object _gate = new();

        public ChannelSink(ChannelWriter<SubscriptionMessage> writer, ILogger logger, string subscriptionId, IEnumerable<string> relays)
        {
            _writer = writer;
            _logger = logger;
            _subscriptionId = subscriptionId;
            _openRelays = new HashSet<string>(relays, StringComparer.Ordinal);
        }

        public void OnEvent(NostrEvent verifiedEvent, RelayUrl relay) => _writer.TryWrite(new EventReceived(verifiedEvent, relay));

        public void OnEndOfStoredEvents(RelayUrl relay) => _writer.TryWrite(new EndOfStoredEvents(relay));

        public void OnRelayDone(RelayUrl relay, string? reason)
        {
            // `reason` may contain relay-authored text; log only its category.
            _logger.LogDebug("Subscription {Subscription}: relay {Relay} done ({Category})", _subscriptionId, relay.Value, reason is null ? "disconnected" : reason.StartsWith("closed by relay", StringComparison.Ordinal) ? "closed by relay" : "disconnected");
            if (reason is null || !reason.StartsWith("closed by relay", StringComparison.Ordinal))
            {
                return; // transient: disconnect / not connected; the relay re-arms the REQ when it comes back
            }

            lock (_gate)
            {
                _openRelays.Remove(relay.Value);
                if (_openRelays.Count == 0)
                {
                    _writer.TryComplete();
                }
            }
        }
    }

    /// <summary>One-shot collector: dedups by id, caps the number of kept events, remembers which relays still owe an EOSE (or dropped out).</summary>
    private sealed class FetchCollector(HashSet<string> pendingRelays, int maxEvents, NostrBackendDiagnostics diagnostics) : ISubscriptionSink
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, NostrEvent> _events = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AllRelaysDone => _done.Task;

        public int PendingCount
        {
            get
            {
                lock (_gate)
                {
                    return pendingRelays.Count;
                }
            }
        }

        public void OnEvent(NostrEvent verifiedEvent, RelayUrl relay)
        {
            lock (_gate)
            {
                if (_events.ContainsKey(verifiedEvent.Id))
                {
                    return;
                }

                if (_events.Count >= maxEvents)
                {
                    diagnostics.CountDroppedBufferedMessage();
                    return;
                }

                _events.Add(verifiedEvent.Id, verifiedEvent);
            }
        }

        public void OnEndOfStoredEvents(RelayUrl relay) => MarkDone(relay);

        public void OnRelayDone(RelayUrl relay, string? reason) => MarkDone(relay);

        private void MarkDone(RelayUrl relay)
        {
            lock (_gate)
            {
                pendingRelays.Remove(relay.Value);
                if (pendingRelays.Count == 0)
                {
                    _done.TrySetResult();
                }
            }
        }

        public IReadOnlyList<NostrEvent> Snapshot()
        {
            lock (_gate)
            {
                return _events.Values.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            }
        }
    }
}
