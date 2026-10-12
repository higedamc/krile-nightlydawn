using NightlyDawn.App.Avatars;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>
/// L6b (plan §26.4): the three things the loader leaves to the App side, measured without Avalonia's
/// rendering stack and without a network. The fake loader answers with a path that does not exist, so every
/// "successful" fetch here decodes to null through the <see cref="IOException"/> arm of
/// <see cref="AvatarImageCache.Decode"/> -- enough to measure sharing, caching and the bound, which are
/// about <i>calls</i>, not pixels. The pixel half (a real PNG landing in the row) is
/// <c>AvatarRenderTests</c> in the render project.
/// </summary>
public class AvatarImageCacheTests
{
    private const string Url = "https://cdn.example/alice.png";

    [Fact]
    public async Task ConcurrentRequestsForOneUrl_ShareASingleFetch()
    {
        var loader = new FakeRemoteImageLoader { Gate = new TaskCompletionSource() };
        var cache = new AvatarImageCache(() => loader);

        var first = cache.GetAsync(Url);
        var second = cache.GetAsync(Url);

        Assert.Equal(1, loader.CallCount);
        Assert.Equal(1, cache.InFlightCount);
        Assert.False(first.IsCompleted);

        loader.Gate.SetResult();
        Assert.Null(await first);
        Assert.Null(await second);
        Assert.Equal(1, loader.CallCount);
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task ACompletedResult_IsServedFromTheCache_EvenWhenItWasAFailure()
    {
        var loader = new FakeRemoteImageLoader();
        var cache = new AvatarImageCache(() => loader);

        Assert.Null(await cache.GetAsync(Url));
        Assert.Null(await cache.GetAsync(Url));

        // A dead URL is asked about once, not once per row that shows the author.
        Assert.Equal(1, loader.CallCount);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task NoLoader_MeansNoFetch_AndNothingCached()
    {
        var loader = new FakeRemoteImageLoader();
        IRemoteImageLoader? current = null;
        var cache = new AvatarImageCache(() => current);

        Assert.Null(await cache.GetAsync(Url));
        Assert.Equal(0, loader.CallCount);
        Assert.Equal(0, cache.Count); // "no loader" is not remembered as "no avatar"
        // The fake was never reachable here: the getter answered null, so the cache had nothing to call. The
        // row-level control -- AppServices.RemoteImageLoader null with a counting fake sitting directly behind
        // the shared cache, so a bypass of the switch is counted -- is AvatarRenderTests.NullLoader_FetchesNothing.

        // Read on every call, not captured: flipping the getter later makes the next request fetch.
        current = loader;
        Assert.Null(await cache.GetAsync(Url));
        Assert.Equal(1, loader.CallCount);
    }

    [Fact]
    public async Task OneCallersCancellation_DoesNotCancelTheSharedFetch()
    {
        var loader = new FakeRemoteImageLoader { Gate = new TaskCompletionSource() };
        var cache = new AvatarImageCache(() => loader);
        using var cts = new CancellationTokenSource();

        var cancelled = cache.GetAsync(Url, cts.Token);
        var patient = cache.GetAsync(Url);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(patient.IsCompleted);
        Assert.Equal(1, cache.InFlightCount);

        loader.Gate.SetResult();
        Assert.Null(await patient);
        Assert.Equal(1, loader.CallCount);
    }

    [Fact]
    public async Task RetainedEntries_AreBounded_LeastRecentlyUsedFirst()
    {
        var loader = new FakeRemoteImageLoader();
        var cache = new AvatarImageCache(() => loader, maximumEntries: 2);

        await cache.GetAsync("https://cdn.example/a.png");
        await cache.GetAsync("https://cdn.example/b.png");
        await cache.GetAsync("https://cdn.example/a.png"); // touch a: b is now the least recently used
        await cache.GetAsync("https://cdn.example/c.png"); // evicts b

        Assert.Equal(2, cache.Count);
        Assert.Equal(3, loader.CallCount);

        await cache.GetAsync("https://cdn.example/a.png"); // still retained: no fetch
        Assert.Equal(3, loader.CallCount);

        await cache.GetAsync("https://cdn.example/b.png"); // evicted: fetched again
        Assert.Equal(4, loader.CallCount);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public async Task AnUnreadableFile_DecodesToNull_NotAnException()
    {
        var loader = new FakeRemoteImageLoader
        {
            Resolve = _ => Path.Combine(Path.GetTempPath(), "nightlydawn-does-not-exist-" + Guid.NewGuid().ToString("N") + ".img"),
        };
        var cache = new AvatarImageCache(() => loader);

        Assert.Null(await cache.GetAsync(Url));
    }

    [Fact]
    public async Task ALoaderBug_SurfacesToTheCaller_AndReleasesTheUrl()
    {
        var calls = 0;
        var loader = new ThrowingLoader(() => calls++ == 0 ? throw new InvalidOperationException("bug") : null);
        var cache = new AvatarImageCache(() => loader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync(Url));
        Assert.Equal(0, cache.InFlightCount);
        Assert.Equal(0, cache.Count); // not negative-cached: a bug is not "no avatar"

        Assert.Null(await cache.GetAsync(Url)); // the URL is askable again
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankUrl_IsACallerBug(string url)
    {
        var cache = new AvatarImageCache(() => new FakeRemoteImageLoader());
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync(url));
    }

    private sealed class ThrowingLoader(Func<string?> answer) : IRemoteImageLoader
    {
        public Task<string?> GetOrFetchAsync(string url, CancellationToken cancellationToken = default) =>
            Task.FromResult(answer());
    }
}
