using NightlyDawn.Core;
using NightlyDawn.Nostr.Mapping;
using NightlyDawn.Nostr.Profiles;
using NightlyDawn.Nostr.Tests.Fakes;
using Xunit;

namespace NightlyDawn.Nostr.Tests;

/// <summary>Plan §8.1: batching, surviving a malformed kind:0 in the batch, newest-created_at-wins, and the
/// negative cache -- each with the mutation that should make its assertion fail.</summary>
public class ProfileStoreTests
{
    private static readonly EventMapper Mapper = new();

    [Fact]
    public async Task PrefetchAsync_ForNDistinctAuthors_IssuesExactlyOneFetchCall()
    {
        var alice = new TestSigner(1);
        var bob = new TestSigner(2);
        var carol = new TestSigner(3);
        var backend = new FakeProfileBackend().Enqueue(
        [
            alice.Sign(0, "{\"name\":\"alice\"}"),
            bob.Sign(0, "{\"name\":\"bob\"}"),
            carol.Sign(0, "{\"name\":\"carol\"}"),
        ]);
        var store = new ProfileStore(backend, Mapper);

        await store.PrefetchAsync([alice.PubkeyHex, bob.PubkeyHex, carol.PubkeyHex]);

        // Mutating ProfileStore back to "one FetchAsync call per pubkey" turns this 1 into 3.
        Assert.Single(backend.FetchCalls);
        var filter = backend.FetchCalls[0];
        Assert.Equal([0], filter.Kinds);
        Assert.Equal(
            new[] { alice.PubkeyHex, bob.PubkeyHex, carol.PubkeyHex },
            filter.Authors,
            StringComparer.Ordinal);

        Assert.Equal("alice", store.TryGet(alice.PubkeyHex)?.Name);
        Assert.Equal("bob", store.TryGet(bob.PubkeyHex)?.Name);
        Assert.Equal("carol", store.TryGet(carol.PubkeyHex)?.Name);
    }

    [Fact]
    public async Task PrefetchAsync_OneMalformedKind0InTheBatch_DoesNotDropTheRestOfIt()
    {
        var alice = new TestSigner(1);
        var brokenPubkey = new TestSigner(2);
        var carol = new TestSigner(3);
        // brokenPubkey's kind:0 has non-JSON content -- EventMapper.ToProfile throws EventMappingException for it.
        var broken = new NostrEvent("broken-id", brokenPubkey.PubkeyHex, 1_700_000_000, 0, [], "not json", "00");
        var backend = new FakeProfileBackend().Enqueue(
        [
            alice.Sign(0, "{\"name\":\"alice\"}"),
            broken,
            carol.Sign(0, "{\"name\":\"carol\"}"),
        ]);
        var store = new ProfileStore(backend, Mapper);

        await store.PrefetchAsync([alice.PubkeyHex, brokenPubkey.PubkeyHex, carol.PubkeyHex]);

        Assert.Equal("alice", store.TryGet(alice.PubkeyHex)?.Name);
        Assert.Equal("carol", store.TryGet(carol.PubkeyHex)?.Name);
        Assert.Null(store.TryGet(brokenPubkey.PubkeyHex)); // Negative-cached: asked, found nothing usable.
    }

    [Fact]
    public async Task PrefetchAsync_NewerCreatedAt_ReplacesTheCachedProfile()
    {
        var alice = new TestSigner(1);
        var backend = new FakeProfileBackend().Enqueue(
        [
            alice.Sign(0, "{\"name\":\"old-name\"}", createdAt: 100),
            alice.Sign(0, "{\"name\":\"new-name\"}", createdAt: 200),
        ]);
        var store = new ProfileStore(backend, Mapper);

        await store.PrefetchAsync([alice.PubkeyHex]);

        // Mutating the replace rule to "last event wins regardless of created_at" would still pass if the
        // newer one happens to be processed last; it is here, which is why the next two tests pin the order.
        Assert.Equal("new-name", store.TryGet(alice.PubkeyHex)?.Name);
    }

    [Fact]
    public async Task PrefetchAsync_OlderCreatedAtArrivingSecond_DoesNotReplaceTheCachedProfile()
    {
        var alice = new TestSigner(1);
        var backend = new FakeProfileBackend().Enqueue(
        [
            alice.Sign(0, "{\"name\":\"new-name\"}", createdAt: 200),
            alice.Sign(0, "{\"name\":\"old-name\"}", createdAt: 100),
        ]);
        var store = new ProfileStore(backend, Mapper);

        await store.PrefetchAsync([alice.PubkeyHex]);

        // Mutating ">" to ">=" (or dropping the comparison) would let the older, second-processed event win here.
        Assert.Equal("new-name", store.TryGet(alice.PubkeyHex)?.Name);
    }

    [Fact]
    public async Task PrefetchAsync_TiedCreatedAtArrivingSecond_DoesNotReplaceTheCachedProfile()
    {
        var alice = new TestSigner(1);
        var backend = new FakeProfileBackend().Enqueue(
        [
            alice.Sign(0, "{\"name\":\"first\"}", createdAt: 100),
            alice.Sign(0, "{\"name\":\"second\"}", createdAt: 100),
        ]);
        var store = new ProfileStore(backend, Mapper);

        await store.PrefetchAsync([alice.PubkeyHex]);

        // Mutating "strictly newer" to "newer-or-equal" would let "second" win this tie.
        Assert.Equal("first", store.TryGet(alice.PubkeyHex)?.Name);
    }

    [Fact]
    public async Task PrefetchAsync_PubkeyWithNoKind0_IsNegativeCached_AndNotReFetched()
    {
        var alice = new TestSigner(1);
        var backend = new FakeProfileBackend().Enqueue([]); // No kind:0 anywhere for alice.
        var store = new ProfileStore(backend, Mapper);

        await store.PrefetchAsync([alice.PubkeyHex]);
        Assert.Null(store.TryGet(alice.PubkeyHex));

        await store.PrefetchAsync([alice.PubkeyHex]); // Same pubkey again.

        // Mutating the "already cached (incl. negative)" filter to only check positive hits would re-issue this.
        Assert.Single(backend.FetchCalls);
    }

    [Fact]
    public async Task PrefetchAsync_AllPubkeysAlreadyCached_IssuesNoFetchCallAtAll()
    {
        var alice = new TestSigner(1);
        var backend = new FakeProfileBackend().Enqueue([alice.Sign(0, "{\"name\":\"alice\"}")]);
        var store = new ProfileStore(backend, Mapper);
        await store.PrefetchAsync([alice.PubkeyHex]);
        Assert.Single(backend.FetchCalls);

        await store.PrefetchAsync([alice.PubkeyHex]);

        Assert.Single(backend.FetchCalls); // Still 1: no empty-filter REQ for an already-resolved pubkey.
    }

    [Fact]
    public void TryGet_UnknownPubkey_ReturnsNull()
    {
        var store = new ProfileStore(new FakeProfileBackend(), Mapper);
        Assert.Null(store.TryGet(new TestSigner(9).PubkeyHex));
    }
}
