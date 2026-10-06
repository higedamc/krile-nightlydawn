namespace NightlyDawn.Nostr.Transport;

/// <summary>
/// One relay socket. Abstracted so tests drive <see cref="Relay.RelayClient"/> against an in-process fake
/// instead of the network, and so the WebSocket implementation can be swapped.
/// </summary>
internal interface IRelayConnection : IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken);

    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>Next text message from the relay, or <c>null</c> when the relay closed the connection.</summary>
    /// <exception cref="RelayMessageTooLargeException">The relay sent a message above the configured size cap; the connection is closed.</exception>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

internal interface IRelayConnectionFactory
{
    IRelayConnection Create(Uri relayUri, int maxMessageBytes);
}

internal sealed class RelayMessageTooLargeException(int limitBytes)
    : Exception($"Relay message exceeded the {limitBytes}-byte limit")
{
    public int LimitBytes { get; } = limitBytes;
}
