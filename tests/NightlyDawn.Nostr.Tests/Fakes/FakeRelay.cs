using System.Text.Json;
using System.Threading.Channels;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Transport;
using NightlyDawn.Nostr.Wire;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>
/// In-process relay. The test scripts how it answers REQ (which events, whether/when to send EOSE) and EVENT
/// (OK true/false), and can push arbitrary raw messages. No sockets, no network.
/// </summary>
internal sealed class FakeRelay
{
    public FakeRelay(string url) => Url = RelayUrl.Parse(url, allowInsecureForDevelopment: true); // tests exercise the ws:// opt-in path too

    public RelayUrl Url { get; }

    public List<NostrEvent> StoredEvents { get; } = [];

    /// <summary>Delay before EOSE; null = never send EOSE.</summary>
    public TimeSpan? EoseDelay { get; set; } = TimeSpan.Zero;

    public bool AcceptPublishes { get; set; } = true;

    public string? RejectReason { get; set; } = "blocked: test";

    /// <summary>When false the relay never answers EVENT with OK (simulates a hung relay).</summary>
    public bool AnswerPublishes { get; set; } = true;

    public bool FailConnect { get; set; }

    public List<string> Received { get; } = [];

    public List<FakeRelayConnection> Connections { get; } = [];

    public int ConnectAttempts { get; private set; }

    public FakeRelayConnection Open()
    {
        ConnectAttempts++;
        var connection = new FakeRelayConnection(this);
        Connections.Add(connection);
        return connection;
    }

    /// <summary>Push a raw message to every open connection (e.g. an unsolicited EVENT or a NOTICE).</summary>
    public void Push(string rawMessage)
    {
        foreach (var c in Connections.Where(c => c.IsOpen))
        {
            c.PushFromRelay(rawMessage);
        }
    }

    public void PushEvent(string subscriptionId, NostrEvent e) =>
        Push($"[\"EVENT\",{JsonSerializer.Serialize(subscriptionId)},{EventJson(e)}]");

    public void CloseSubscription(string subscriptionId, string reason) =>
        Push($"[\"CLOSED\",{JsonSerializer.Serialize(subscriptionId)},{JsonSerializer.Serialize(reason)}]");

    public static string EventJson(NostrEvent e)
    {
        var message = NostrJson.EventMessage(e); // ["EVENT",{...}]
        return message[("[\"EVENT\",").Length..^1];
    }

    internal async Task HandleClientMessageAsync(FakeRelayConnection connection, string message)
    {
        Received.Add(message);
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        switch (root[0].GetString())
        {
            case "REQ":
                var subId = root[1].GetString()!;
                foreach (var e in StoredEvents)
                {
                    connection.PushFromRelay($"[\"EVENT\",{JsonSerializer.Serialize(subId)},{EventJson(e)}]");
                }

                if (EoseDelay is { } delay)
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }

                    connection.PushFromRelay($"[\"EOSE\",{JsonSerializer.Serialize(subId)}]");
                }

                break;
            case "EVENT":
                if (!AnswerPublishes)
                {
                    break;
                }

                var id = root[1].GetProperty("id").GetString()!;
                var ok = AcceptPublishes ? "true" : "false";
                var reason = AcceptPublishes ? "\"\"" : JsonSerializer.Serialize(RejectReason ?? "");
                connection.PushFromRelay($"[\"OK\",{JsonSerializer.Serialize(id)},{ok},{reason}]");
                break;
        }
    }
}

internal sealed class FakeRelayConnection(FakeRelay relay) : IRelayConnection
{
    private readonly Channel<string?> _fromRelay = Channel.CreateUnbounded<string?>();
    private int _maxMessageBytes = int.MaxValue;

    public bool IsOpen { get; private set; }

    internal void Configure(int maxMessageBytes) => _maxMessageBytes = maxMessageBytes;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (relay.FailConnect)
        {
            throw new InvalidOperationException("fake relay refuses connections");
        }

        IsOpen = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("connection closed");
        }

        _ = relay.HandleClientMessageAsync(this, message);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var message = await _fromRelay.Reader.ReadAsync(cancellationToken);
        if (message is not null && System.Text.Encoding.UTF8.GetByteCount(message) > _maxMessageBytes)
        {
            IsOpen = false;
            throw new RelayMessageTooLargeException(_maxMessageBytes);
        }

        return message;
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        IsOpen = false;
        _fromRelay.Writer.TryWrite(null);
        return Task.CompletedTask;
    }

    /// <summary>Relay side: deliver a message to the client.</summary>
    public void PushFromRelay(string message) => _fromRelay.Writer.TryWrite(message);

    /// <summary>Relay side: hang up.</summary>
    public void Disconnect()
    {
        IsOpen = false;
        _fromRelay.Writer.TryWrite(null);
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeRelayFactory : IRelayConnectionFactory
{
    private readonly Dictionary<string, FakeRelay> _relays = new(StringComparer.Ordinal);

    public FakeRelay Add(string url)
    {
        var relay = new FakeRelay(url);
        _relays[url] = relay;
        return relay;
    }

    public IRelayConnection Create(Uri relayUri, int maxMessageBytes)
    {
        var key = relayUri.ToString().TrimEnd('/');
        if (!_relays.TryGetValue(key, out var relay))
        {
            throw new InvalidOperationException($"no fake relay registered for {key}");
        }

        var connection = relay.Open();
        connection.Configure(maxMessageBytes);
        return connection;
    }
}
