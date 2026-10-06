using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Transport;
using NightlyDawn.Nostr.Wire;

namespace NightlyDawn.Nostr.Relay;

/// <summary>What a subscription wants to hear from one relay. Implemented by the pool's multiplexers.</summary>
internal interface ISubscriptionSink
{
    void OnEvent(NostrEvent verifiedEvent, RelayUrl relay);

    void OnEndOfStoredEvents(RelayUrl relay);

    /// <summary>The relay stopped serving this subscription (CLOSED from the relay, or the connection dropped).</summary>
    void OnRelayDone(RelayUrl relay, string? reason);
}

/// <summary>
/// One relay: connect with timeout, receive loop, reconnect with backoff, REQ/CLOSE/EVENT plumbing,
/// OK correlation for publishes. Every inbound event is verified (id + BIP-340) before a sink sees it.
/// Logs never contain event content, filters or NOTICE text — only relay URL, kinds, ids (prefix) and counts.
/// </summary>
internal sealed class RelayClient : IAsyncDisposable
{
    private readonly IRelayConnectionFactory _connections;
    private readonly NostrBackendOptions _options;
    private readonly NostrBackendDiagnostics _diagnostics;
    private readonly ILogger _logger;
    private readonly Uri _uri;
    private readonly ConcurrentDictionary<string, (NostrFilter Filter, ISubscriptionSink Sink)> _subscriptions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(bool Accepted, string? Reason)>> _pendingOks = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private IRelayConnection? _connection;
    private Task? _receiveLoop;
    private volatile bool _connected;
    private int _reconnectAttempt;

    public RelayClient(RelayUrl url, IRelayConnectionFactory connections, NostrBackendOptions options, NostrBackendDiagnostics diagnostics, ILogger logger)
    {
        Url = url;
        _connections = connections;
        _options = options;
        _diagnostics = diagnostics;
        _logger = logger;
        if (!Uri.TryCreate(url.Value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            !(uri.Scheme == "wss" || (uri.Scheme == "ws" && options.AllowInsecureWebSockets)))
        {
            throw new RelayConnectionException(url.Value, $"Relay URL is not an acceptable wss:// URL: {url.Value}");
        }

        _uri = uri;
    }

    public RelayUrl Url { get; }

    public bool IsConnected => _connected;

    /// <summary>Connects (with <see cref="NostrBackendOptions.ConnectTimeout"/>) and starts the receive loop. Failure throws <see cref="RelayConnectionException"/>; the caller decides whether to keep the relay in the pool and retry.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                return;
            }

            var connection = _connections.Create(_uri, _options.MaxMessageBytes);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(_options.ConnectTimeout);
            try
            {
                await connection.ConnectAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw new RelayConnectionException(Url.Value, $"Connect to {Url.Value} failed: {ex.GetType().Name}");
            }

            _connection = connection;
            _connected = true;
            _reconnectAttempt = 0;
            _receiveLoop = Task.Run(() => ReceiveLoopAsync(connection, _lifetime.Token), CancellationToken.None);
            _logger.LogInformation("Connected to relay {Relay}", Url.Value);

            // Re-arm live subscriptions after a reconnect.
            foreach (var (subscriptionId, (filter, _)) in _subscriptions)
            {
                await SendAsync(NostrJson.ReqMessage(subscriptionId, filter), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Starts the backoff reconnect loop (used by the pool when the initial connect fails).</summary>
    public void ScheduleReconnect()
    {
        if (!_lifetime.IsCancellationRequested)
        {
            _ = Task.Run(() => ReconnectLoopAsync(_lifetime.Token), CancellationToken.None);
        }
    }

    public async Task SubscribeAsync(string subscriptionId, NostrFilter filter, ISubscriptionSink sink, CancellationToken cancellationToken)
    {
        _subscriptions[subscriptionId] = (filter, sink);
        if (_connected)
        {
            await SendAsync(NostrJson.ReqMessage(subscriptionId, filter), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Not connected: the sink must not wait for an EOSE that will never come.
            sink.OnRelayDone(Url, "not connected");
        }
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        if (_subscriptions.TryRemove(subscriptionId, out _) && _connected)
        {
            await SendAsync(NostrJson.CloseMessage(subscriptionId), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Sends the event and waits for this relay's <c>OK</c>, or <see cref="NostrBackendOptions.PublishTimeout"/>.</summary>
    public async Task<RelayPublishOutcome> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken)
    {
        if (!_connected)
        {
            return new RelayPublishOutcome(Url, false, "not connected");
        }

        var tcs = new TaskCompletionSource<(bool Accepted, string? Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingOks[signedEvent.Id] = tcs;
        try
        {
            await SendAsync(NostrJson.EventMessage(signedEvent), cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.PublishTimeout);
            var (accepted, reason) = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new RelayPublishOutcome(Url, accepted, Truncate(reason));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RelayPublishOutcome(Url, false, "timeout");
        }
        catch (RelayConnectionException ex)
        {
            return new RelayPublishOutcome(Url, false, ex.Message);
        }
        finally
        {
            _pendingOks.TryRemove(signedEvent.Id, out _);
        }
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || !_connected)
        {
            throw new RelayConnectionException(Url.Value, "not connected");
        }

        try
        {
            await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new RelayConnectionException(Url.Value, $"Send to {Url.Value} failed: {ex.GetType().Name}");
        }
    }

    private async Task ReceiveLoopAsync(IRelayConnection connection, CancellationToken lifetime)
    {
        string? disconnectReason = null;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var message = await connection.ReceiveAsync(lifetime).ConfigureAwait(false);
                if (message is null)
                {
                    disconnectReason = "closed by relay";
                    break;
                }

                Dispatch(message);
            }
        }
        catch (RelayMessageTooLargeException)
        {
            _diagnostics.CountOversizeMessage();
            _logger.LogWarning("Relay {Relay} sent a message over {Limit} bytes; connection closed", Url.Value, _options.MaxMessageBytes);
            disconnectReason = "oversize message";
        }
        catch (OperationCanceledException)
        {
            disconnectReason = "disposed";
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Relay {Relay} receive loop ended: {Error}", Url.Value, ex.GetType().Name);
            disconnectReason = ex.GetType().Name;
        }

        _connected = false;
        await connection.DisposeAsync().ConfigureAwait(false);
        FailPendingPublishes(disconnectReason ?? "disconnected");
        foreach (var (_, (_, sink)) in _subscriptions)
        {
            sink.OnRelayDone(Url, disconnectReason);
        }

        if (!lifetime.IsCancellationRequested)
        {
            _ = Task.Run(() => ReconnectLoopAsync(lifetime), CancellationToken.None);
        }
    }

    private void Dispatch(string message)
    {
        var parsed = NostrJson.ParseRelayMessage(message);
        switch (parsed)
        {
            case null:
                _diagnostics.CountMalformedMessage();
                _logger.LogDebug("Relay {Relay}: malformed message ({Length} bytes) dropped", Url.Value, message.Length);
                break;
            case RelayMessage.Event e:
                HandleEvent(e);
                break;
            case RelayMessage.Eose eose:
                if (_subscriptions.TryGetValue(eose.SubscriptionId, out var eoseSub))
                {
                    eoseSub.Sink.OnEndOfStoredEvents(Url);
                }

                break;
            case RelayMessage.Ok ok:
                if (_pendingOks.TryGetValue(ok.EventId, out var tcs))
                {
                    tcs.TrySetResult((ok.Accepted, ok.Reason));
                }

                break;
            case RelayMessage.Closed closed:
                if (_subscriptions.TryRemove(closed.SubscriptionId, out var closedSub))
                {
                    // The relay's reason text is relay-authored: forwarded (truncated) to the sink, never logged.
                    _logger.LogInformation("Relay {Relay} closed subscription {Subscription} (reason length {Length})", Url.Value, closed.SubscriptionId, closed.Reason?.Length ?? 0);
                    closedSub.Sink.OnRelayDone(Url, closed.Reason is null ? "closed by relay" : "closed by relay: " + Truncate(closed.Reason));
                }

                break;
            case RelayMessage.Notice notice:
                _logger.LogDebug("Relay {Relay}: NOTICE ({Length} chars)", Url.Value, notice.Length);
                break;
            case RelayMessage.Auth:
                _logger.LogDebug("Relay {Relay}: AUTH requested (NIP-42 not supported in 1a)", Url.Value);
                break;
        }
    }

    private void HandleEvent(RelayMessage.Event message)
    {
        if (!_subscriptions.TryGetValue(message.SubscriptionId, out var sub))
        {
            return; // late event for a subscription we already closed
        }

        var e = message.Payload;
        switch (EventVerifier.Verify(e))
        {
            case VerificationResult.Valid:
                sub.Sink.OnEvent(e, Url);
                break;
            case VerificationResult.IdMismatch:
                _diagnostics.CountInvalidId();
                _logger.LogWarning("Relay {Relay}: event {EventId} kind {Kind} dropped — id mismatch", Url.Value, Prefix(e.Id), e.Kind);
                break;
            case VerificationResult.InvalidSignature:
                _diagnostics.CountInvalidSignature();
                _logger.LogWarning("Relay {Relay}: event {EventId} kind {Kind} dropped — invalid signature", Url.Value, Prefix(e.Id), e.Kind);
                break;
            default:
                _diagnostics.CountMalformedMessage();
                _logger.LogDebug("Relay {Relay}: event dropped — malformed fields", Url.Value);
                break;
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            var attempt = Interlocked.Increment(ref _reconnectAttempt);
            var delayTicks = Math.Min(_options.ReconnectMaxDelay.Ticks, _options.ReconnectInitialDelay.Ticks * (1L << Math.Min(attempt - 1, 10)));
            try
            {
                await Task.Delay(TimeSpan.FromTicks(delayTicks), lifetime).ConfigureAwait(false);
                await ConnectAsync(lifetime).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RelayConnectionException)
            {
                _logger.LogDebug("Relay {Relay}: reconnect attempt {Attempt} failed", Url.Value, attempt);
            }
        }
    }

    private void FailPendingPublishes(string reason)
    {
        foreach (var (id, tcs) in _pendingOks)
        {
            tcs.TrySetResult((false, reason));
            _pendingOks.TryRemove(id, out _);
        }
    }

    private static string Prefix(string id) => id.Length >= 8 ? id[..8] : id;

    private string? Truncate(string? relayText) =>
        relayText is null || relayText.Length <= _options.MaxRelayReasonChars ? relayText : relayText[.._options.MaxRelayReasonChars];

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        var connection = _connection;
        if (connection is not null)
        {
            try
            {
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connected = false;
        FailPendingPublishes("disposed");
        _connectLock.Dispose();
        _lifetime.Dispose();
    }
}
