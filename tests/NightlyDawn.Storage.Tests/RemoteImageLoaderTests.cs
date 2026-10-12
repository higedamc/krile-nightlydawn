using System.Net;
using Xunit;

namespace NightlyDawn.Storage.Tests;

/// <summary>
/// Adversarial tests for <see cref="RemoteImageLoader"/>. A profile's <c>picture</c> URL is written by a
/// stranger, so each case here is something a hostile profile would actually put there, run against a real
/// loopback server over real sockets.
/// </summary>
public sealed class RemoteImageLoaderTests : IDisposable
{
    private readonly string _cacheDirectory =
        Path.Combine(Path.GetTempPath(), "nightlydawn-image-cache-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_cacheDirectory))
        {
            Directory.Delete(_cacheDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task FetchesAnImageOnceAndServesTheSecondCallFromDisk()
    {
        var png = TestHttpServer.Png(512);
        await using var server = new TestHttpServer(context => TestHttpServer.WriteAsync(context, png));
        using var loader = NewLoader();

        var first = await loader.GetOrFetchAsync(server.Url("/avatar.png"));
        var second = await loader.GetOrFetchAsync(server.Url("/avatar.png"));

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(png, await File.ReadAllBytesAsync(first!));
        Assert.Equal(1, server.Requests); // The second call must not touch the network at all.
    }

    /// <summary>A relay-supplied string must not be able to steer the write out of the cache directory, or pick
    /// a filename at all: the entry is named by a hash of the URL.</summary>
    [Theory]
    [InlineData("/../../../../etc/passwd")]
    [InlineData("/a%2F..%2F..%2Fb.png")]
    [InlineData("/.ssh/authorized_keys")]
    public async Task TheCachedPathIsAlwaysInsideTheCacheDirectory(string path)
    {
        var png = TestHttpServer.Png();
        await using var server = new TestHttpServer(context => TestHttpServer.WriteAsync(context, png));
        using var loader = NewLoader();

        var result = await loader.GetOrFetchAsync(server.Url(path));

        if (result is null)
        {
            return; // The server may 404 a path like this; what matters is that nothing escaped.
        }

        Assert.Equal(Path.GetFullPath(_cacheDirectory), Path.GetDirectoryName(Path.GetFullPath(result)));
        Assert.EndsWith(".img", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAResponseThatIsNotAnImage()
    {
        await using var server = new TestHttpServer(context =>
        {
            TestHttpServer.Text(context, "<html><script>alert(1)</script></html>");
            return Task.CompletedTask;
        });
        using var loader = NewLoader();

        var result = await loader.GetOrFetchAsync(server.Url("/not-an-image"));

        Assert.Null(result);
        AssertCacheIsEmpty();
    }

    /// <summary>The cap has to bite while streaming, so the body is sent chunked: there is no
    /// <c>Content-Length</c> for the loader to short-circuit on, and the response starts with a valid PNG
    /// signature so the signature check cannot be what rejects it.</summary>
    [Fact]
    public async Task AbandonsABodyThatGrowsPastTheCapWhileStreaming()
    {
        var chunk = TestHttpServer.Png(16 * 1024);
        await using var server = new TestHttpServer(context => TestHttpServer.WriteChunkedAsync(context, chunk, times: 64));
        using var loader = NewLoader(maximumBytes: 64 * 1024);

        var result = await loader.GetOrFetchAsync(server.Url("/bomb.png"));

        Assert.Null(result);
        AssertCacheIsEmpty(); // Includes the .part file: an abandoned fetch must leave nothing behind.
    }

    [Fact]
    public async Task RejectsADeclaredLengthOverTheCap()
    {
        var png = TestHttpServer.Png(200 * 1024);
        await using var server = new TestHttpServer(context => TestHttpServer.WriteAsync(context, png));
        using var loader = NewLoader(maximumBytes: 64 * 1024);

        Assert.Null(await loader.GetOrFetchAsync(server.Url("/big.png")));
        AssertCacheIsEmpty();
    }

    [Fact]
    public async Task FollowsAnHttpRedirectToTheImage()
    {
        var png = TestHttpServer.Png();
        await using var server = new TestHttpServer(context =>
        {
            if (context.Request.Url!.AbsolutePath == "/moved")
            {
                TestHttpServer.Redirect(context, "/real.png");
                return Task.CompletedTask;
            }

            return TestHttpServer.WriteAsync(context, png);
        });
        using var loader = NewLoader();

        var result = await loader.GetOrFetchAsync(server.Url("/moved"));

        Assert.NotNull(result);
        Assert.Equal(png, await File.ReadAllBytesAsync(result!));
        Assert.Equal(2, server.Requests);
    }

    /// <summary>The contract's "http(s) only, <i>including after a redirect</i>". A scheme check that only ran
    /// on the URL the profile supplied would pass this test's first hop and then fetch the second.</summary>
    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.invalid/avatar.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    public async Task RejectsARedirectAwayFromHttp(string location)
    {
        await using var server = new TestHttpServer(context =>
        {
            TestHttpServer.Redirect(context, location);
            return Task.CompletedTask;
        });
        using var loader = NewLoader();

        Assert.Null(await loader.GetOrFetchAsync(server.Url("/moved")));
        AssertCacheIsEmpty();
    }

    [Fact]
    public async Task StopsFollowingAfterAFewRedirects()
    {
        await using var server = new TestHttpServer(context =>
        {
            TestHttpServer.Redirect(context, "/loop");
            return Task.CompletedTask;
        });
        using var loader = NewLoader();

        Assert.Null(await loader.GetOrFetchAsync(server.Url("/loop")));
        Assert.InRange(server.Requests, 1, 8); // Bounded, not "until the heat death of the universe".
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.invalid/a.png")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url at all")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsANonHttpUrlWithoutContactingAnything(string url)
    {
        await using var server = new TestHttpServer(context => TestHttpServer.WriteAsync(context, TestHttpServer.Png()));
        using var loader = NewLoader();

        Assert.Null(await loader.GetOrFetchAsync(url));
        Assert.Equal(0, server.Requests);
    }

    /// <summary>
    /// The production default, asserted rather than assumed -- and the negative control for every test above:
    /// they only reach the loopback server because they pass <see cref="AllowAnyAddressPolicy"/>. With the real
    /// default the same fetch must fail, which is what proves the address check is doing the work and the other
    /// tests are not quietly succeeding for some other reason.
    /// </summary>
    [Fact]
    public async Task TheDefaultPolicyRefusesToDialLoopback()
    {
        await using var server = new TestHttpServer(context => TestHttpServer.WriteAsync(context, TestHttpServer.Png()));
        using var loader = new RemoteImageLoader(_cacheDirectory); // No policy argument: production default.

        Assert.Null(await loader.GetOrFetchAsync(server.Url("/avatar.png")));
        Assert.Equal(0, server.Requests); // Refused before the socket, not after a response.
        AssertCacheIsEmpty();
    }

    [Fact]
    public async Task GivesUpOnAServerThatStopsSendingMidBody()
    {
        using var serverStopping = new CancellationTokenSource();
        await using var server = new TestHttpServer(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.SendChunked = true;
            await context.Response.OutputStream.WriteAsync(TestHttpServer.Png(32));
            await context.Response.OutputStream.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), serverStopping.Token);
        });
        using var loader = NewLoader(timeout: TimeSpan.FromMilliseconds(400));

        Assert.Null(await loader.GetOrFetchAsync(server.Url("/slow.png")));
        AssertCacheIsEmpty();
        await serverStopping.CancelAsync(); // Release the stalled handler so disposing the server does not wait on it.
    }

    [Fact]
    public void RefusesACapTooSmallToEvenIdentifyAnImage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RemoteImageLoader(_cacheDirectory, maximumBytes: 4));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RefusesAnEmptyCacheDirectory(string directory)
    {
        Assert.Throws<ArgumentException>(() => new RemoteImageLoader(directory));
    }

    private RemoteImageLoader NewLoader(int maximumBytes = RemoteImageLoader.DefaultMaximumBytes, TimeSpan? timeout = null) =>
        new(_cacheDirectory, new AllowAnyAddressPolicy(), maximumBytes, timeout ?? TimeSpan.FromSeconds(10));

    private void AssertCacheIsEmpty()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return;
        }

        var leftovers = Directory.GetFileSystemEntries(_cacheDirectory);
        Assert.True(leftovers.Length == 0,
            $"the cache directory should be empty after a rejected fetch but holds: {string.Join(", ", leftovers.Select(Path.GetFileName))}");
    }
}
