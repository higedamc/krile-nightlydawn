using NightlyDawn.Core;
using NightlyDawn.Nostr.ContactLists;
using NightlyDawn.Nostr.Tests.Fakes;
using Xunit;

namespace NightlyDawn.Nostr.Tests.ContactLists;

/// <summary>Plan §13.2 item 4: kind:3 resolution for <c>from home</c> — no event and an event with no "p" tags
/// both mean "follows nobody" (never null), newest <c>created_at</c> wins, and a malformed "p" tag is dropped
/// rather than propagated (relays are adversarial input, B13). Each assertion names the mutation that would
/// make it fail.</summary>
public class NostrContactListSourceTests
{
    private static readonly TestSigner Alice = new(1);

    [Fact]
    public async Task FetchContactListAsync_NoContactListEvent_ReturnsEmpty_NotNull()
    {
        var backend = new FakeProfileBackend(); // enqueues nothing: FetchAsync returns []
        var source = new NostrContactListSource(backend);

        var follows = await source.FetchContactListAsync(Alice.PubkeyHex);

        // Mutating this to `return null!;` would NRE every caller that does `follows.Contains(...)`.
        Assert.NotNull(follows);
        Assert.Empty(follows);
    }

    [Fact]
    public async Task FetchContactListAsync_FiltersByKindAndAuthor()
    {
        var backend = new FakeProfileBackend();
        var source = new NostrContactListSource(backend);

        await source.FetchContactListAsync(Alice.PubkeyHex);

        var filter = Assert.Single(backend.FetchCalls);
        // Mutating Kinds away from [3] would also match e.g. kind:0 profile events on the same subscription.
        Assert.Equal([3], filter.Kinds);
        Assert.Equal([Alice.PubkeyHex], filter.Authors, StringComparer.Ordinal);
    }

    [Fact]
    public async Task FetchContactListAsync_NewestCreatedAtWins_AcrossSeveralContactListEvents()
    {
        var bob = new TestSigner(2);
        var carol = new TestSigner(3);
        var backend = new FakeProfileBackend().Enqueue(
        [
            Alice.Sign(3, "", tags: [["p", bob.PubkeyHex]], createdAt: 100), // older: followed only bob
            Alice.Sign(3, "", tags: [["p", carol.PubkeyHex]], createdAt: 200), // newest: followed only carol
        ]);
        var source = new NostrContactListSource(backend);

        var follows = await source.FetchContactListAsync(Alice.PubkeyHex);

        // Mutating MaxBy to MinBy (or dropping it for "first") would return bob instead.
        Assert.Equal([carol.PubkeyHex], follows);
    }

    [Fact]
    public async Task FetchContactListAsync_MalformedPTag_IsDroppedNotPropagated()
    {
        var bob = new TestSigner(2);
        var backend = new FakeProfileBackend().Enqueue(
        [
            Alice.Sign(
                3,
                "",
                tags:
                [
                    ["p", bob.PubkeyHex],
                    ["p", "not-a-hex-pubkey"],
                    ["p", "ab12"], // valid hex, wrong length
                    ["e", bob.PubkeyHex], // right shape, wrong tag name
                ],
                createdAt: 100),
        ]);
        var source = new NostrContactListSource(backend);

        var follows = await source.FetchContactListAsync(Alice.PubkeyHex);

        // Mutating away the IsLowerHex/length guard would let "not-a-hex-pubkey" or "ab12" through.
        Assert.Equal([bob.PubkeyHex], follows);
    }

    [Fact]
    public async Task FetchContactListAsync_DuplicatePTags_AreDeduplicated()
    {
        var bob = new TestSigner(2);
        var backend = new FakeProfileBackend().Enqueue(
        [
            Alice.Sign(3, "", tags: [["p", bob.PubkeyHex], ["p", bob.PubkeyHex]], createdAt: 100),
        ]);
        var source = new NostrContactListSource(backend);

        var follows = await source.FetchContactListAsync(Alice.PubkeyHex);

        Assert.Equal([bob.PubkeyHex], follows);
    }

    [Fact]
    public async Task FetchContactListAsync_RejectsAnEmptyPubkey()
    {
        var source = new NostrContactListSource(new FakeProfileBackend());

        await Assert.ThrowsAsync<ArgumentException>(() => source.FetchContactListAsync(""));
    }
}
