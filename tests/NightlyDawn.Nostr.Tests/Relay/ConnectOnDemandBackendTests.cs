using NightlyDawn.Core;
using NightlyDawn.Nostr.Relay;
using NightlyDawn.Nostr.Tests.Fakes;
using Xunit;

namespace NightlyDawn.Nostr.Tests.Relay;

/// <summary>
/// L3c: the publisher's backend connects the pool before it publishes, and does nothing else differently.
/// The ordering assertions are written against a shared log rather than two independent flags, because
/// "connect was called" and "connect was called <em>first</em>" are different claims and only the second one
/// is the point.
/// </summary>
public sealed class ConnectOnDemandBackendTests
{
    private static NostrEvent SignedEvent() => new(
        Id: new string('a', 64),
        Pubkey: new string('b', 64),
        CreatedAt: 1_700_000_000,
        Kind: 1,
        Tags: [],
        Content: "hello",
        Sig: new string('c', 128));

    [Fact]
    public async Task PublishAsync_ConnectsBeforeItPublishes()
    {
        var log = new List<string>();
        var inner = new RecordingBackend(log);
        var sut = new ConnectOnDemandBackend(inner, _ =>
        {
            log.Add("connect");
            return Task.CompletedTask;
        });

        await sut.PublishAsync(SignedEvent());

        Assert.Equal(["connect", "publish"], log);
    }

    [Fact]
    public async Task PublishAsync_WhenConnectFails_PropagatesAndNeverPublishes()
    {
        var inner = new FakeNostrBackend();
        var sut = new ConnectOnDemandBackend(inner, _ =>
            Task.FromException(new RelayConnectionException("wss://unreachable.example/", "boom")));

        var ex = await Assert.ThrowsAsync<RelayConnectionException>(() => sut.PublishAsync(SignedEvent()));

        Assert.Contains("boom", ex.Message);
        Assert.Empty(inner.PublishedEvents);
    }

    [Fact]
    public async Task PublishAsync_ReturnsTheInnerResultUnchanged()
    {
        var inner = new FakeNostrBackend();
        var sut = new ConnectOnDemandBackend(inner, _ => Task.CompletedTask);

        var result = await sut.PublishAsync(SignedEvent());

        Assert.True(result.AnyAccepted);
        Assert.Single(inner.PublishedEvents);
    }

    /// <summary>The read paths already connect through their own callback, so wrapping them here would dial
    /// twice. FakeNostrBackend throws NotSupportedException from its read members, so reaching that exception
    /// is itself the evidence the call was delegated rather than handled here.</summary>
    [Fact]
    public async Task ReadPaths_AreNotIntercepted_AndDoNotConnect()
    {
        var connects = 0;
        var sut = new ConnectOnDemandBackend(new FakeNostrBackend(), _ =>
        {
            connects++;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => sut.FetchAsync(new NostrFilter(Kinds: [1])));
        await Assert.ThrowsAsync<NotSupportedException>(() => sut.FetchProfileEventAsync(new string('b', 64)));
        await Assert.ThrowsAsync<NotSupportedException>(() => sut.FetchRelayListAsync(new string('b', 64)));

        Assert.Equal(0, connects);
    }

    [Fact]
    public void Constructor_RejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ConnectOnDemandBackend(null!, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new ConnectOnDemandBackend(new FakeNostrBackend(), null!));
    }

    private sealed class RecordingBackend(List<string> log) : INostrBackend
    {
        public Task<PublishResult> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default)
        {
            log.Add("publish");
            return Task.FromResult(new PublishResult([new RelayPublishOutcome(RelayUrl.Parse("wss://fake.example/"), Accepted: true)]));
        }

        public Task ConnectAsync(IReadOnlyCollection<RelayUrl> relayUrls, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SubscriptionMessage> SubscribeAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<NostrEvent>> FetchAsync(NostrFilter filter, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
