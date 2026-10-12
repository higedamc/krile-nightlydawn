using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NightlyDawn.Core;

namespace NightlyDawn.Storage;

/// <summary>
/// <see cref="IRemoteImageLoader"/> over HTTP with a disk cache (L6a). The one sanctioned path from a
/// relay-supplied <c>picture</c> URL to bytes on disk.
///
/// <para><b>Every defence here is enforced, not documented</b> (the contract's wording). In order of what an
/// adversarial profile would try:</para>
/// <list type="number">
/// <item><description><b>Scheme.</b> http and https only, re-checked on every redirect hop, so a redirect to
/// <c>file:///etc/passwd</c> or a custom scheme handler is not reachable.</description></item>
/// <item><description><b>Address.</b> The connection is dialled by this class, and every resolved address is
/// put to <see cref="IRemoteAddressPolicy"/> before the socket is opened. The address approved is the address
/// used, so a name that resolves public at validation time and loopback at connect time (DNS rebinding) does
/// not get through. <c>UseProxy</c> is off for the same reason: a proxy would do the connecting, and this
/// check would be inspecting the proxy's address instead of the target's.</description></item>
/// <item><description><b>Size.</b> Counted while streaming and aborted mid-download, so a decompression bomb
/// or an endless response never fully materialises -- neither in memory nor on disk. A declared
/// <c>Content-Length</c> over the cap short-circuits, but it is never <i>trusted</i>: the streaming count runs
/// regardless, because a server that lies about the length is exactly the case the cap is for.</description></item>
/// <item><description><b>Time.</b> One budget for the whole operation, redirects and body read included, not a
/// per-request timeout that a chain of slow hops can multiply.</description></item>
/// <item><description><b>Content.</b> The first bytes must match a container we accept
/// (<see cref="ImageSignature"/>), checked before the body is written, so HTML, a script or an executable
/// never lands in the cache directory.</description></item>
/// <item><description><b>Compression.</b> <c>AutomaticDecompression</c> is off and no
/// <c>Accept-Encoding</c> is sent, so the bytes counted are the bytes stored. A server that compresses anyway
/// fails the signature check.</description></item>
/// </list>
///
/// <para><b>Failure is always null, never an exception</b> for anything the remote end controls -- a rejected
/// URL, a refused connection, a timeout, an oversized body, an unrecognised container. An avatar is
/// decoration. A bug in <i>this</i> code is still allowed to throw: the catches below name the exception types
/// a network boundary produces rather than swallowing everything.</para>
///
/// <para><b>What this does not do, on purpose.</b> It does not decode the image -- the App layer does, with
/// Avalonia's decoder, and that call has to tolerate failure too, because a file passing the signature check
/// can still be a corrupt or hostile image. It does not expire entries: a cached avatar is served until the
/// cache directory is deleted, which is the refresh mechanism in v1. It does not bound the directory's total
/// size. Both are follow-ups, written down in the plan rather than half-done here.</para>
///
/// <para><b>Privacy note the UI needs to own.</b> Fetching an avatar reveals the operator's IP address to
/// whatever host the profile names, which is a per-author beacon. That is inherent to remote avatars and is
/// true of every Nostr client that shows them; it is called out here so that whoever wires this into the row
/// view decides deliberately whether avatars load for everyone or only for followed authors.</para>
/// </summary>
public sealed class RemoteImageLoader : IRemoteImageLoader, IDisposable
{
    /// <summary>2 MiB. Comfortably above any sane avatar and far below what would hurt to download or store.</summary>
    public const int DefaultMaximumBytes = 2 * 1024 * 1024;

    /// <summary>Whole-operation budget, redirects included.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Enough for the usual CDN hop or two; small enough that a redirect loop ends immediately.</summary>
    private const int MaximumRedirects = 3;

    private readonly string _cacheDirectory;
    private readonly int _maximumBytes;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _client;

    /// <param name="cacheDirectory">Where fetched images are kept. Created on first use.</param>
    /// <param name="addressPolicy">Which addresses may be dialled. Defaults to
    /// <see cref="PublicUnicastOnlyPolicy"/>; a test passes a permissive one to reach a loopback server.</param>
    /// <param name="maximumBytes">Hard cap on a single response body.</param>
    /// <param name="timeout">Whole-operation budget.</param>
    public RemoteImageLoader(
        string cacheDirectory,
        IRemoteAddressPolicy? addressPolicy = null,
        int maximumBytes = DefaultMaximumBytes,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, ImageSignature.MaximumPrefixLength);

        _cacheDirectory = cacheDirectory;
        _maximumBytes = maximumBytes;
        _timeout = timeout ?? DefaultTimeout;

        var policy = addressPolicy ?? new PublicUnicastOnlyPolicy();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,     // Redirects are followed by hand so each hop is re-validated.
            UseCookies = false,            // Nothing here is authenticated; a cookie jar is only a tracking surface.
            UseProxy = false,              // See the class remarks: a proxy would void the address check.
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = (context, token) => ConnectAsync(policy, context, token),
        };

        _client = new HttpClient(handler, disposeHandler: true);
        // Timeouts are driven by the per-call CancellationTokenSource, which also covers the body read; the
        // client-level timeout would apply per request and so multiply across redirect hops.
        _client.Timeout = Timeout.InfiniteTimeSpan;
        _client.DefaultRequestHeaders.Accept.ParseAdd("image/*");
    }

    public async Task<string?> GetOrFetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !TryParseHttpUrl(url, out var uri))
        {
            return null;
        }

        var cachedPath = Path.Combine(_cacheDirectory, CacheFileName(url));
        if (File.Exists(cachedPath))
        {
            return cachedPath;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);

        try
        {
            return await FetchAsync(uri, cachedPath, budget.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or OperationCanceledException or UriFormatException or UnauthorizedAccessException)
        {
            // Everything the far end can cause, plus a cache directory that turns out not to be writable.
            // Callers see "no avatar", which is what they would show anyway; an avatar load must never be the
            // thing that takes down a fire-and-forget UI task.
            return null;
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Opens the socket for a request, after putting every resolved address to the policy. Returning a
    /// stream is the only way out: a rejected host throws, which <see cref="HttpClient"/> surfaces as
    /// <see cref="HttpRequestException"/> and <see cref="GetOrFetchAsync"/> turns into null.</summary>
    private static async ValueTask<Stream> ConnectAsync(IRemoteAddressPolicy policy, SocketsHttpConnectionContext context, CancellationToken token)
    {
        var endPoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, token).ConfigureAwait(false);
        var allowed = addresses.Where(policy.IsAllowed).ToArray();

        if (allowed.Length == 0)
        {
            throw new HttpRequestException(
                $"no address for '{endPoint.Host}' is permitted for a remote image fetch ({addresses.Length} resolved)");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect to the approved addresses only. Handing the hostname to ConnectAsync here would re-resolve
            // and could pick an address the policy never saw.
            await socket.ConnectAsync(allowed, endPoint.Port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<string?> FetchAsync(Uri uri, string cachedPath, CancellationToken token)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (hop >= MaximumRedirects)
                {
                    return null;
                }

                var location = response.Headers.Location;
                if (location is null)
                {
                    return null;
                }

                // Relative Location is legal, so resolve against the hop we are on; then the scheme check runs
                // again on the result. This is the step that makes "http(s) only, including after a redirect"
                // true rather than aspirational.
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsHttpScheme(next))
                {
                    return null;
                }

                uri = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is { } declared && declared > _maximumBytes)
            {
                return null;
            }

            return await StoreAsync(response, cachedPath, token).ConfigureAwait(false);
        }
    }

    private async Task<string?> StoreAsync(HttpResponseMessage response, string cachedPath, CancellationToken token)
    {
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

        var prefix = new byte[ImageSignature.MaximumPrefixLength];
        var prefixLength = await ReadAtLeastAsync(body, prefix, token).ConfigureAwait(false);
        if (!ImageSignature.IsRecognised(prefix.AsSpan(0, prefixLength)))
        {
            return null;
        }

        Directory.CreateDirectory(_cacheDirectory);

        // Written under a unique temporary name and moved into place, so a reader never sees a half-written
        // file and two concurrent fetches of the same URL cannot interleave into one.
        var temporaryPath = cachedPath + ".part-" + Guid.NewGuid().ToString("n");
        var moved = false;
        try
        {
            var total = prefixLength;
            await using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await file.WriteAsync(prefix.AsMemory(0, prefixLength), token).ConfigureAwait(false);

                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await body.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > _maximumBytes)
                    {
                        // Abandoned mid-stream: the point of the cap is that the rest is never read or stored.
                        return null;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
            }

            File.Move(temporaryPath, cachedPath, overwrite: true);
            moved = true;
            return cachedPath;
        }
        finally
        {
            if (!moved)
            {
                // Covers every early return above (cap exceeded) and every throw: a rejected fetch must not
                // leave a .part file behind for someone to mistake for a cache entry.
                TryDelete(temporaryPath);
            }
        }
    }

    /// <summary>Fills as much of <paramref name="buffer"/> as the stream offers, returning the byte count. A
    /// single <c>ReadAsync</c> may return one byte of a perfectly good PNG, which would fail the signature
    /// check for the wrong reason.</summary>
    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
               or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool TryParseHttpUrl(string url, out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !IsHttpScheme(parsed))
        {
            uri = null!;
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool IsHttpScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>The cache name is a hash of the URL, never any part of the URL itself: a relay-supplied string
    /// must not be able to choose a path (<c>../</c>), a filename the filesystem treats specially, or a name
    /// that collides with another URL's entry on a case-insensitive volume.</summary>
    private static string CacheFileName(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".img";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .part file is harmless; failing to clean one up must not fail the fetch.
        }
    }
}
