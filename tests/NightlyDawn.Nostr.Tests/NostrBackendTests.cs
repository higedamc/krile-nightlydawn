using NightlyDawn.Core;
using NightlyDawn.Nostr.Relay;
using NightlyDawn.Nostr.Tests.Fakes;
using NightlyDawn.Nostr.Wire;
using Xunit;

namespace NightlyDawn.Nostr.Tests;

public class NostrBackendTests
{
    private static readonly TestSigner Signer = new();

    private static (NostrBackend Backend, FakeRelayFactory Factory) NewBackend(NostrBackendOptions? options = null)
    {
        var factory = new FakeRelayFactory();
        var backend = new NostrBackend(
            factory,
            options ?? new NostrBackendOptions { FetchTimeout = TimeSpan.FromSeconds(5), PublishTimeout = TimeSpan.FromMilliseconds(500) });
        return (backend, factory);
    }

    [Fact]
    public async Task Fetch_WaitsForEoseFromEveryConnectedRelay_NotJustTheFirst()
    {
        var (backend, factory) = NewBackend();
        var fast = factory.Add("wss://fast.example");
        var slow = factory.Add("wss://slow.example");
        var onlyOnSlow = Signer.Sign(1, "late but important", createdAt: 1_700_000_001);
        fast.StoredEvents.Add(Signer.Sign(1, "early", createdAt: 1_700_000_000));
        slow.StoredEvents.Add(onlyOnSlow);
        slow.EoseDelay = TimeSpan.FromMilliseconds(400);

        await backend.ConnectAsync([fast.Url, slow.Url]);
        var started = DateTime.UtcNow;
        var events = await backend.FetchAsync(new NostrFilter(Kinds: [1]));

        // Negative control: with a first-EOSE break this returns after `fast` alone and never contains the slow relay's event.
        Assert.Contains(events, e => e.Id == onlyOnSlow.Id);
        Assert.Equal(2, events.Count);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(350), "fetch returned before the slow relay's EOSE");
        Assert.Equal(onlyOnSlow.Id, events[0].Id); // newest first
    }

    [Fact]
    public async Task Fetch_DedupsTheSameEventSeenOnTwoRelays_AndStopsAtTheTimeoutIfARelayNeverSendsEose()
    {
        var (backend, factory) = NewBackend(new NostrBackendOptions { FetchTimeout = TimeSpan.FromMilliseconds(300) });
        var a = factory.Add("wss://a.example");
        var b = factory.Add("wss://b.example");
        var shared = Signer.Sign(1, "shared");
        a.StoredEvents.Add(shared);
        b.StoredEvents.Add(shared);
        b.EoseDelay = null; // never

        await backend.ConnectAsync([a.Url, b.Url]);
        var events = await backend.FetchAsync(new NostrFilter(Kinds: [1]));

        Assert.Single(events);
        Assert.Contains(b.Received, m => m.StartsWith("[\"CLOSE\"", StringComparison.Ordinal)); // cleaned up even on timeout
    }

    [Fact]
    public async Task Publish_ReportsPerRelayOutcomes_AndThrowsOnlyWhenNoRelayAccepted()
    {
        var (backend, factory) = NewBackend();
        var accepting = factory.Add("wss://ok.example");
        var rejecting = factory.Add("wss://no.example");
        rejecting.AcceptPublishes = false;
        rejecting.RejectReason = "blocked: spam";
        var hung = factory.Add("wss://hung.example");
        hung.AnswerPublishes = false;
        await backend.ConnectAsync([accepting.Url, rejecting.Url, hung.Url]);

        var result = await backend.PublishAsync(Signer.Sign(1, "hi"));

        Assert.True(result.AnyAccepted);
        Assert.Equal(3, result.Outcomes.Count);
        Assert.True(result.Outcomes.Single(o => o.RelayUrl == accepting.Url).Accepted);
        var rejected = result.Outcomes.Single(o => o.RelayUrl == rejecting.Url);
        Assert.False(rejected.Accepted);
        Assert.Equal("blocked: spam", rejected.Reason);
        var timedOut = result.Outcomes.Single(o => o.RelayUrl == hung.Url);
        Assert.False(timedOut.Accepted);
        Assert.Equal("timeout", timedOut.Reason);

        // Negative control for the c121754a class: 0-of-N must be an exception, not a result that looks like success.
        accepting.AcceptPublishes = false;
        var ex = await Assert.ThrowsAsync<EventPublishException>(() => backend.PublishAsync(Signer.Sign(1, "again")));
        Assert.False(ex.Result.AnyAccepted);
        Assert.Equal(3, ex.Result.Outcomes.Count);
    }

    [Fact]
    public async Task Subscribe_DropsEventsWithBadSignatureOrId_AndCountsThem()
    {
        var (backend, factory) = NewBackend();
        var relay = factory.Add("wss://evil.example");
        var good = Signer.Sign(1, "genuine");
        var otherKey = new TestSigner(9).PubkeyHex;
        var impersonation = good with { Pubkey = otherKey, Id = EventVerifier.ComputeId(otherKey, good.CreatedAt, 1, good.Tags, good.Content) };
        var tampered = good with { Content = "genuine (edited)" };
        relay.StoredEvents.AddRange([impersonation, tampered, good]);
        await backend.ConnectAsync([relay.Url]);

        var received = new List<SubscriptionMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in backend.SubscribeAsync(new NostrFilter(Kinds: [1]), cts.Token))
        {
            received.Add(message);
            if (message is EndOfStoredEvents)
            {
                break;
            }
        }

        var events = received.OfType<EventReceived>().ToList();
        Assert.Single(events);
        Assert.Equal(good.Id, events[0].Event.Id);
        Assert.Equal(relay.Url, events[0].RelayUrl);
        Assert.Equal(1, backend.Diagnostics.InvalidSignature);
        Assert.Equal(1, backend.Diagnostics.InvalidId);
    }

    [Fact]
    public async Task Connect_RejectsInsecureWebSocketsUnlessOptedIn()
    {
        var (backend, factory) = NewBackend();
        factory.Add("ws://localhost:7777");
        var insecure = RelayUrl.Parse("ws://localhost:7777", allowInsecureForDevelopment: true);

        await Assert.ThrowsAsync<RelayConnectionException>(() => backend.ConnectAsync([insecure]));

        var (devBackend, devFactory) = NewBackend(new NostrBackendOptions { AllowInsecureWebSockets = true });
        devFactory.Add("ws://localhost:7777");
        await devBackend.ConnectAsync([insecure]);
        Assert.Single(devBackend.ConnectedRelays);
    }

    [Fact]
    public async Task Connect_SucceedsIfAnyRelayConnects_AndThrowsWhenNoneDo()
    {
        var (backend, factory) = NewBackend();
        var up = factory.Add("wss://up.example");
        var down = factory.Add("wss://down.example");
        down.FailConnect = true;

        await backend.ConnectAsync([up.Url, down.Url]);
        Assert.Equal([up.Url], backend.ConnectedRelays);

        var (allDown, f2) = NewBackend();
        f2.Add("wss://x.example").FailConnect = true;
        await Assert.ThrowsAsync<RelayConnectionException>(() => allDown.ConnectAsync([RelayUrl.Parse("wss://x.example")]));
    }

    [Fact]
    public async Task OversizeMessage_ClosesTheConnection_AndIsCounted()
    {
        var (backend, factory) = NewBackend(new NostrBackendOptions { MaxMessageBytes = 2048, ReconnectInitialDelay = TimeSpan.FromMinutes(5) });
        var relay = factory.Add("wss://big.example");
        await backend.ConnectAsync([relay.Url]);

        relay.Push("[\"NOTICE\",\"" + new string('x', 4096) + "\"]");
        await WaitUntilAsync(() => backend.Diagnostics.OversizeMessages == 1);

        Assert.Empty(backend.ConnectedRelays);
    }

    [Fact]
    public async Task Subscribe_EndsWithSubscriptionClosed_WhenEveryRelayClosesIt()
    {
        var (backend, factory) = NewBackend();
        var relay = factory.Add("wss://closer.example");
        await backend.ConnectAsync([relay.Url]);

        var messages = new List<SubscriptionMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in backend.SubscribeAsync(new NostrFilter(Kinds: [1]), cts.Token))
        {
            messages.Add(message);
            if (message is EndOfStoredEvents)
            {
                var req = relay.Received.First(m => m.StartsWith("[\"REQ\"", StringComparison.Ordinal));
                var subId = System.Text.Json.JsonDocument.Parse(req).RootElement[1].GetString()!;
                relay.CloseSubscription(subId, "rate limited");
            }
        }

        Assert.IsType<SubscriptionClosed>(messages[^1]);
    }

    [Fact]
    public async Task FetchProfile_ReturnsNewestKind0_FromTheRequestedAuthorOnly()
    {
        var (backend, factory) = NewBackend();
        var relay = factory.Add("wss://p.example");
        var older = Signer.Sign(0, "{\"name\":\"old\"}", createdAt: 100);
        var newer = Signer.Sign(0, "{\"name\":\"new\"}", createdAt: 200);
        var someoneElse = new TestSigner(3).Sign(0, "{\"name\":\"other\"}", createdAt: 300);
        relay.StoredEvents.AddRange([older, someoneElse, newer]);
        await backend.ConnectAsync([relay.Url]);

        var profileEvent = await backend.FetchProfileEventAsync(Signer.PubkeyHex);

        Assert.Equal(newer.Id, profileEvent!.Id);
        Assert.Equal("new", backend.Mapper.ToProfile(profileEvent).Name);
    }

    [Fact]
    public async Task Fetch_CapsKeptEvents_AndTruncatesRelayReasons()
    {
        var (backend, factory) = NewBackend(new NostrBackendOptions { MaxEventsPerFetch = 3, MaxRelayReasonChars = 10, PublishTimeout = TimeSpan.FromMilliseconds(500) });
        var relay = factory.Add("wss://flood.example");
        for (var i = 0; i < 10; i++)
        {
            relay.StoredEvents.Add(Signer.Sign(1, $"note {i}", createdAt: 1_700_000_000 + i));
        }

        relay.AcceptPublishes = false;
        relay.RejectReason = "blocked: a very long relay-authored explanation that should be cut";
        await backend.ConnectAsync([relay.Url]);

        var events = await backend.FetchAsync(new NostrFilter(Kinds: [1]));
        Assert.Equal(3, events.Count);
        Assert.Equal(7, backend.Diagnostics.DroppedBufferedMessages);

        var ex = await Assert.ThrowsAsync<EventPublishException>(() => backend.PublishAsync(Signer.Sign(1, "x")));
        Assert.Equal("blocked: a", ex.Result.Outcomes.Single().Reason);
    }

    [Fact]
    public async Task PoisonMessage_CostsOneMessage_NotTheConnection()
    {
        var (backend, factory) = NewBackend();
        var relay = factory.Add("wss://poison.example");
        var good = Signer.Sign(1, "still alive");
        await backend.ConnectAsync([relay.Url]);

        var received = new List<SubscriptionMessage>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var message in backend.SubscribeAsync(new NostrFilter(Kinds: [1]), cts.Token))
        {
            received.Add(message);
            if (message is EndOfStoredEvents)
            {
                var req = relay.Received.First(m => m.StartsWith("[\"REQ\"", StringComparison.Ordinal));
                var subId = System.Text.Json.JsonDocument.Parse(req).RootElement[1].GetString()!;
                // Lone high surrogate: JsonDocument.Parse accepts it, GetString() throws InvalidOperationException.
                relay.Push("[\"EVENT\",\"" + subId + "\",{\"id\":\"ab\",\"pubkey\":\"cd\",\"created_at\":1,\"kind\":1,\"tags\":[],\"content\":\"\\uD800\",\"sig\":\"ef\"}]");
                relay.Push("[\"NOTICE\",\"\\uD800\"]");
                relay.PushEvent(subId, good);
            }

            if (message is EventReceived)
            {
                break;
            }
        }

        // Negative control: without the per-message try/catch in the receive loop the poison event tears the
        // connection down (ConnectAttempts becomes 2 after the reconnect) and the counter stays at 0.
        Assert.Equal(good.Id, received.OfType<EventReceived>().Single().Event.Id);
        Assert.Equal(2, backend.Diagnostics.MalformedMessages);
        Assert.Equal(0, backend.Diagnostics.DispatchFaults); // relay garbage is not a fault of ours
        Assert.Equal(1, relay.ConnectAttempts);
        Assert.Single(backend.ConnectedRelays);
    }

    [Fact]
    public async Task Publish_WithNoConnectedRelays_IsAConnectivityFailure_NotARejection()
    {
        var (backend, factory) = NewBackend();
        factory.Add("wss://down.example").FailConnect = true;
        await Assert.ThrowsAsync<RelayConnectionException>(() => backend.ConnectAsync([RelayUrl.Parse("wss://down.example")]));

        // Negative control: without the guard this surfaces as EventPublishException("No relay accepted"), i.e. offline reads as rejected.
        await Assert.ThrowsAsync<RelayConnectionException>(() => backend.PublishAsync(Signer.Sign(1, "offline")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("condition not met in time");
            }

            await Task.Delay(20);
        }
    }
}
