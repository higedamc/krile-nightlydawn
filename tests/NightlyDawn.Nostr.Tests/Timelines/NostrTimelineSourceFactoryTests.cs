using NightlyDawn.Core;
using NightlyDawn.Nostr.Tests.Fakes;
using NightlyDawn.Nostr.Timelines;
using Xunit;

namespace NightlyDawn.Nostr.Tests.Timelines;

/// <summary>
/// L3c: the factory can hand out a publisher over its own backend. Nothing here opens a socket — the factory
/// connects lazily, and constructing a publisher is not a publish.
///
/// <para>"Constructing a publisher does not dial the pool" is deliberately not asserted here:
/// <see cref="NostrBackendDiagnostics"/> has no connected-relay counter, so there is nothing to observe, and a
/// test asserting an unrelated counter would read as coverage while measuring nothing. Where connecting
/// <em>is</em> observable — on the publish path — it is covered by
/// <c>ConnectOnDemandBackendTests</c>.</para>
/// </summary>
public sealed class NostrTimelineSourceFactoryTests
{
    private static NostrTimelineSourceFactory NewFactory() =>
        new([RelayUrl.Parse("wss://relay.example/")]);

    [Fact]
    public async Task CreatePublisher_ReturnsAPublisher()
    {
        await using var factory = NewFactory();

        var publisher = factory.CreatePublisher(new FakeKeyStore());

        Assert.NotNull(publisher);
    }

    [Fact]
    public async Task CreatePublisher_RejectsANullKeyStore()
    {
        await using var factory = NewFactory();

        Assert.Throws<ArgumentNullException>(() => factory.CreatePublisher(null!));
    }

    /// <summary>Two publishers from one factory are separate objects over the same backend. The sharing is the
    /// point (one relay pool for reading and writing, plan §8/§10) and is asserted on the backend being a
    /// private field of a single factory instance rather than by reaching into it.</summary>
    [Fact]
    public async Task CreatePublisher_CanBeCalledMoreThanOnce()
    {
        await using var factory = NewFactory();
        var keyStore = new FakeKeyStore();

        var first = factory.CreatePublisher(keyStore);
        var second = factory.CreatePublisher(keyStore);

        Assert.NotSame(first, second);
    }
}
