using NightlyDawn.Core;
using NightlyDawn.Keys;
using NightlyDawn.Nostr.Timelines;
using Xunit;

namespace NightlyDawn.Host.Tests;

/// <summary>
/// L3c: the host's one wiring decision. <c>Program.Main</c> has no seam a test can reach, so the decision
/// lives in <see cref="PublisherWiring"/> and is asserted here. The key store is a real
/// <see cref="LocalKeyStore"/> over a path in the temp directory rather than a stub: nothing in this leaf
/// signs, so a store that merely exists is the honest input, and a 12-member fake of
/// <see cref="IKeyStore"/> would assert nothing extra.
/// </summary>
public sealed class PublisherWiringTests
{
    private static NostrTimelineSourceFactory NewFactory() =>
        new([RelayUrl.Parse("wss://relay.example/")]);

    private static IKeyStore NewKeyStore() =>
        new LocalKeyStore(new FileKeyFileStore(Path.Combine(Path.GetTempPath(), $"krile-l3c-{Guid.NewGuid():N}.json")));

    /// <summary>No key store is a real state — the host leaves it null when it could not create a data
    /// directory — and the App layer then reports publishing as unavailable up front. A publisher that cannot
    /// sign would trade that for a failure after the user has typed.</summary>
    [Fact]
    public async Task Create_WithoutAKeyStore_ReturnsNull()
    {
        await using var factory = NewFactory();

        Assert.Null(PublisherWiring.Create(factory, keyStore: null));
    }

    [Fact]
    public async Task Create_WithAKeyStore_ReturnsAPublisher()
    {
        await using var factory = NewFactory();

        Assert.NotNull(PublisherWiring.Create(factory, NewKeyStore()));
    }

    [Fact]
    public void Create_RejectsANullFactory()
    {
        Assert.Throws<ArgumentNullException>(() => PublisherWiring.Create(null!, NewKeyStore()));
    }
}
