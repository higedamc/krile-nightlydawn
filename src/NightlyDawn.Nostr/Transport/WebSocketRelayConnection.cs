using System.Net.WebSockets;
using System.Text;

namespace NightlyDawn.Nostr.Transport;

internal sealed class WebSocketRelayConnectionFactory : IRelayConnectionFactory
{
    public IRelayConnection Create(Uri relayUri, int maxMessageBytes) => new WebSocketRelayConnection(relayUri, maxMessageBytes);
}

internal sealed class WebSocketRelayConnection(Uri relayUri, int maxMessageBytes) : IRelayConnection
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Task ConnectAsync(CancellationToken cancellationToken) => _socket.ConnectAsync(relayUri, cancellationToken);

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var accumulated = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                // Binary frames are not part of NIP-01; drop the message but keep the connection.
                if (result.EndOfMessage)
                {
                    accumulated.SetLength(0);
                }

                continue;
            }

            if (accumulated.Length + result.Count > maxMessageBytes)
            {
                await TryCloseAsync(WebSocketCloseStatus.PolicyViolation, "message too large", cancellationToken).ConfigureAwait(false);
                throw new RelayMessageTooLargeException(maxMessageBytes);
            }

            accumulated.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(accumulated.GetBuffer(), 0, (int)accumulated.Length);
            }
        }
    }

    public Task CloseAsync(CancellationToken cancellationToken) => TryCloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);

    private async Task TryCloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                // Peer already gone; nothing to do.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
