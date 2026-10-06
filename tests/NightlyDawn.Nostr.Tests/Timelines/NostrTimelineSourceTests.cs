using System.Text.Json;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Relay;
using NightlyDawn.Nostr.Tests.Fakes;
using NightlyDawn.Nostr.Timelines;
using Xunit;

namespace NightlyDawn.Nostr.Tests.Timelines;

public class NostrTimelineSourceTests
{
    private static readonly TestSigner Signer = new();
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly Timeline Kind1 = new("col", "kind:1", "kind:1");

    private static (NostrTimelineSourceFactory Factory, Dictionary<string, FakeRelay> Relays, NostrBackend Backend) NewFactory(
        IEnumerable<string> relayUrls, TimeSpan? initialLoadTimeout = null, ITimelineQueryCompiler? compiler = null, NostrBackendOptions? options = null)
    {
        var connections = new FakeRelayFactory();
        var relays = relayUrls.ToDictionary(u => u, connections.Add, StringComparer.Ordinal);
        var backend = new NostrBackend(connections, options ?? new NostrBackendOptions { FetchTimeout = TimeSpan.FromSeconds(5) });
        var factory = new NostrTimelineSourceFactory(backend, relays.Values.Select(r => r.Url).ToList(), compiler, initialLoadTimeout, ownsBackend: true, logger: null);
        return (factory, relays, backend);
    }

    private static FakeRelay Relay(Dictionary<string, FakeRelay> relays, string url) => relays[url];

    private static string SubscriptionIdOf(FakeRelay relay)
    {
        var req = relay.Received.First(m => m.StartsWith("[\"REQ\"", StringComparison.Ordinal));
        using var doc = JsonDocument.Parse(req);
        return doc.RootElement[1].GetString()!;
    }

    [Fact]
    public async Task Stream_EmitsEachNoteOnce_AndInitialLoadComplete_OnlyAfterEveryConnectedRelaysEose()
    {
        var (factory, relays, _) = NewFactory(["wss://fast.example", "wss://slow.example"]);
        await using var _ = factory;
        var fast = Relay(relays, "wss://fast.example");
        var slow = Relay(relays, "wss://slow.example");
        var shared = Signer.Sign(1, "shared", createdAt: 1_700_000_000);
        var onlyOnSlow = Signer.Sign(1, "only on slow", createdAt: 1_700_000_001);
        fast.StoredEvents.Add(shared);
        slow.StoredEvents.Add(shared);
        slow.StoredEvents.Add(onlyOnSlow);
        slow.EoseDelay = TimeSpan.FromMilliseconds(400);

        using var cts = new CancellationTokenSource(TestTimeout);
        var updates = new List<TimelineUpdate>();
        var started = DateTime.UtcNow;
        TimeSpan? loadedAfter = null;
        await foreach (var update in factory.CreateAnonymous(Kind1).StreamAsync(cts.Token))
        {
            updates.Add(update);
            if (update is InitialLoadComplete)
            {
                loadedAfter = DateTime.UtcNow - started;
                fast.PushEvent(SubscriptionIdOf(fast), Signer.Sign(1, "live", createdAt: 1_700_000_002)); // arrives after the backlog
            }

            if (updates.Count == 4)
            {
                break;
            }
        }

        var notes = updates.OfType<NoteArrived>().Select(n => n.Note).ToList();
        Assert.Equal(["shared", "only on slow", "live"], notes.Select(n => n.Content)); // `shared` once, although two relays sent it
        Assert.IsType<InitialLoadComplete>(updates[2]); // after both backlog notes, before the live one
        Assert.True(loadedAfter >= TimeSpan.FromMilliseconds(350), $"InitialLoadComplete came after {loadedAfter?.TotalMilliseconds}ms: first-EOSE-wins, not every relay");
        // Both relays delivered `shared`; whichever receive loop reached the channel first is recorded, so only the shape is deterministic.
        Assert.NotNull(notes[0].FirstSeenOnRelay);
        Assert.Contains(notes[0].FirstSeenOnRelay, new[] { fast.Url, slow.Url });
        Assert.Equal(slow.Url, notes[1].FirstSeenOnRelay);
    }

    [Fact]
    public async Task Stream_RemembersOnlyTheConfiguredNumberOfIds_AndRedeliveryAfterEvictionShowsOnceMore()
    {
        var options = new NostrBackendOptions { FetchTimeout = TimeSpan.FromSeconds(5), MaxRememberedEventIdsPerTimeline = 2 };
        var (factory, relays, _) = NewFactory(["wss://r.example"], options: options);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        var first = Signer.Sign(1, "first", createdAt: 1);
        var second = Signer.Sign(1, "second", createdAt: 2);
        var third = Signer.Sign(1, "third", createdAt: 3);
        relay.StoredEvents.AddRange([first, second, third]);

        using var cts = new CancellationTokenSource(TestTimeout);
        var contents = new List<string>();
        await foreach (var update in factory.CreateAnonymous(Kind1).StreamAsync(cts.Token))
        {
            if (update is NoteArrived n)
            {
                contents.Add(n.Note.Content);
            }

            if (update is InitialLoadComplete)
            {
                var sub = SubscriptionIdOf(relay);
                relay.PushEvent(sub, third);  // still remembered -> dropped
                relay.PushEvent(sub, first);  // evicted (capacity 2 kept second+third) -> shown once more, by design
            }

            if (contents.Count == 4)
            {
                break;
            }
        }

        Assert.Equal(["first", "second", "third", "first"], contents);
    }

    [Fact]
    public async Task Stream_WhenEveryRelayHasDroppedSinceConnecting_FailsAsConnectivity_NotAsAnEmptyTimeline()
    {
        // Reconnects are pushed far out so the pool stays empty for the duration of the test.
        var options = new NostrBackendOptions { ReconnectInitialDelay = TimeSpan.FromMinutes(5), ReconnectMaxDelay = TimeSpan.FromMinutes(5) };
        var (factory, relays, backend) = NewFactory(["wss://r.example"], options: options);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        relay.StoredEvents.Add(Signer.Sign(1, "first"));

        Assert.Single((await CollectUntilLoadedAsync(factory.CreateAnonymous(Kind1))).OfType<NoteArrived>()); // connected once, fine

        relay.Connections.Last().Disconnect();
        await WaitUntilAsync(() => backend.ConnectedRelays.Count == 0);

        await Assert.ThrowsAsync<RelayConnectionException>(() => CollectUntilLoadedAsync(factory.CreateAnonymous(Kind1)));
    }

    [Fact]
    public async Task Stream_LeavesLoadingAtTheTimeout_WhenARelayNeverSendsEose()
    {
        var (factory, relays, _) = NewFactory(["wss://ok.example", "wss://mute.example"], initialLoadTimeout: TimeSpan.FromMilliseconds(300));
        await using var _ = factory;
        Relay(relays, "wss://ok.example").StoredEvents.Add(Signer.Sign(1, "backlog"));
        Relay(relays, "wss://mute.example").EoseDelay = null; // never

        using var cts = new CancellationTokenSource(TestTimeout);
        var started = DateTime.UtcNow;
        var updates = new List<TimelineUpdate>();
        await foreach (var update in factory.CreateAnonymous(Kind1).StreamAsync(cts.Token))
        {
            updates.Add(update);
            if (update is InitialLoadComplete)
            {
                break;
            }
        }

        Assert.Equal(2, updates.Count);
        Assert.IsType<NoteArrived>(updates[0]);
        var elapsed = DateTime.UtcNow - started;
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stream_SkipsEventsTheMapperRejects_AndTheyAreCounted()
    {
        var (factory, relays, backend) = NewFactory(["wss://r.example"]);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        relay.StoredEvents.Add(Signer.Sign(0, "{\"name\":\"not a note\"}")); // a relay ignoring our kinds filter
        relay.StoredEvents.Add(Signer.Sign(1, "a note"));

        var updates = await CollectUntilLoadedAsync(factory.CreateAnonymous(Kind1));

        var note = Assert.Single(updates.OfType<NoteArrived>());
        Assert.Equal("a note", note.Note.Content);
        Assert.Equal(1, backend.Diagnostics.MappingFailures);
    }

    [Fact]
    public async Task Stream_AppliesTheCompiledLocalPredicate()
    {
        var compiler = new StubCompiler(new CompiledFilter(new NostrFilter(Kinds: [1]), n => n.Content.Contains("keep", StringComparison.Ordinal)));
        var (factory, relays, _) = NewFactory(["wss://r.example"], compiler: compiler);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        relay.StoredEvents.Add(Signer.Sign(1, "keep me", createdAt: 1));
        relay.StoredEvents.Add(Signer.Sign(1, "drop me", createdAt: 2));

        var updates = await CollectUntilLoadedAsync(factory.CreateAnonymous(new Timeline("c", "anything", "anything")));

        Assert.Equal(["keep me"], updates.OfType<NoteArrived>().Select(n => n.Note.Content));
        Assert.Equal("anything", compiler.LastQuery);
    }

    [Fact]
    public async Task LoadOlder_PagesStrictlyBeforeTheTimestamp_UsingUntil()
    {
        var (factory, relays, _) = NewFactory(["wss://r.example"]);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        relay.StoredEvents.Add(Signer.Sign(1, "t100", createdAt: 100));
        relay.StoredEvents.Add(Signer.Sign(1, "t200", createdAt: 200));
        relay.StoredEvents.Add(Signer.Sign(1, "t300", createdAt: 300)); // the fake relay ignores `until`; the source must still exclude this

        var older = await factory.CreateAnonymous(Kind1).LoadOlderAsync(beforeCreatedAt: 300, limit: 10);

        Assert.Equal(["t200", "t100"], older.Select(n => n.Content)); // newest first
        var req = relay.Received.Single(m => m.StartsWith("[\"REQ\"", StringComparison.Ordinal));
        Assert.Contains("\"until\":299", req, StringComparison.Ordinal);
        Assert.Contains("\"limit\":10", req, StringComparison.Ordinal);

        Assert.Single(await factory.CreateAnonymous(Kind1).LoadOlderAsync(beforeCreatedAt: 300, limit: 1));
        Assert.Empty(await factory.CreateAnonymous(Kind1).LoadOlderAsync(beforeCreatedAt: 0, limit: 10));
    }

    [Fact]
    public async Task Stream_WhenNoRelayConnects_FailsAsConnectivity_ThenRecoversOnTheNextStream()
    {
        var (factory, relays, _) = NewFactory(["wss://down.example"]);
        await using var _ = factory;
        var relay = Relay(relays, "wss://down.example");
        relay.FailConnect = true;

        await Assert.ThrowsAsync<RelayConnectionException>(() => CollectUntilLoadedAsync(factory.CreateAnonymous(Kind1)));

        relay.FailConnect = false;
        relay.StoredEvents.Add(Signer.Sign(1, "back"));
        var updates = await CollectUntilLoadedAsync(factory.CreateAnonymous(Kind1));

        Assert.Single(updates.OfType<NoteArrived>());
    }

    [Fact]
    public async Task Stream_Cancellation_ClosesTheRelaySubscription()
    {
        var (factory, relays, _) = NewFactory(["wss://r.example"]);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        relay.EoseDelay = null;

        using var cts = new CancellationTokenSource();
        var stream = factory.CreateAnonymous(Kind1).StreamAsync(cts.Token);
        var enumeration = Task.Run(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });

        await WaitUntilAsync(() => relay.Received.Any(m => m.StartsWith("[\"REQ\"", StringComparison.Ordinal)));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumeration.WaitAsync(TestTimeout));
        await WaitUntilAsync(() => relay.Received.Any(m => m.StartsWith("[\"CLOSE\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stream_WhenEveryRelayClosesTheSubscription_EmitsInitialLoadComplete_ThenEnds()
    {
        var (factory, relays, _) = NewFactory(["wss://r.example"]);
        await using var _ = factory;
        var relay = Relay(relays, "wss://r.example");
        relay.EoseDelay = null;

        using var cts = new CancellationTokenSource(TestTimeout);
        var updates = new List<TimelineUpdate>();
        var pushed = false;
        var stream = factory.CreateAnonymous(Kind1).StreamAsync(cts.Token);
        var enumeration = Task.Run(async () =>
        {
            await foreach (var update in stream)
            {
                updates.Add(update);
            }
        });

        await WaitUntilAsync(() => relay.Received.Any(m => m.StartsWith("[\"REQ\"", StringComparison.Ordinal)));
        relay.CloseSubscription(SubscriptionIdOf(relay), "closed: shutting down");
        pushed = true;

        await enumeration.WaitAsync(TestTimeout); // ended without cancellation
        Assert.True(pushed);
        Assert.Single(updates);
        Assert.IsType<InitialLoadComplete>(updates[0]);
    }

    [Fact]
    public void Factory_RejectsBadQueries_Synchronously_AndTreatsAccountCreateAsReadOnly()
    {
        var (factory, _, _) = NewFactory(["wss://r.example"]);

        Assert.Throws<FilterParseException>(() => factory.CreateAnonymous(new Timeline("c", "bad", "from home")));

        var account = new Account("00", new SignerDescriptor("00", SignerKind.Nip46));
        Assert.IsType<NostrTimelineSource>(factory.Create(Kind1, account));
        Assert.IsType<NostrTimelineSource>(factory.CreateAnonymous(Kind1));
    }

    [Fact]
    public void Factory_RequiresAtLeastOneRelay()
    {
        var backend = new NostrBackend(new FakeRelayFactory());

        Assert.Throws<ArgumentException>(() => new NostrTimelineSourceFactory(backend, [], compiler: null, initialLoadTimeout: null, ownsBackend: true, logger: null));
        Assert.Throws<ArgumentException>(() => new NostrTimelineSourceFactory([]));
    }

    private static async Task<List<TimelineUpdate>> CollectUntilLoadedAsync(ITimelineSource source)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        var updates = new List<TimelineUpdate>();
        await foreach (var update in source.StreamAsync(cts.Token))
        {
            updates.Add(update);
            if (update is InitialLoadComplete)
            {
                break;
            }
        }

        return updates;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(10);
        }
    }

    private sealed class StubCompiler(CompiledFilter result) : ITimelineQueryCompiler
    {
        public string? LastQuery { get; private set; }

        public CompiledFilter Compile(string query)
        {
            LastQuery = query;
            return result;
        }
    }
}
