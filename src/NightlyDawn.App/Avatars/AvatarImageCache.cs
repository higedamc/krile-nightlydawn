using Avalonia;
using Avalonia.Media.Imaging;
using NightlyDawn.Core;
using SkiaSharp;

namespace NightlyDawn.App.Avatars;

/// <summary>
/// The App layer's half of the avatar pipeline (L6b, plan §26.4): turns a profile's <c>picture</c> URL into
/// an Avalonia <see cref="Bitmap"/> the row view can display, by way of <see cref="IRemoteImageLoader"/> --
/// the <b>only</b> path from a relay-supplied URL to bytes. This class never opens a socket and never looks at
/// the URL: scheme, address, size, time and container checks are all enforced on the loader's side
/// (<c>NightlyDawn.Storage.RemoteImageLoader</c>). What the loader explicitly leaves to this side, and what
/// this class therefore owns:
/// <list type="number">
/// <item><description><b>Decoding tolerates failure, and the header is read before any pixel is allocated.</b>
/// A file that passed the container-signature check can still be corrupt or hostile, and the loader's byte
/// cap bounds bytes, not pixels: a 794-byte GIF can declare 65535x65535 and ask the decoder for 16 GB (plan
/// §27.2-27.3, measured). So <see cref="Decode"/> first reads the dimensions from the header with
/// <see cref="SKCodec"/> -- which allocates no pixels and answers garbage with null rather than an exception --
/// and refuses anything over <see cref="MaximumSourceDimension"/> on either side, or outside an avatar's shape.
/// Only then does it hand the bytes to Avalonia's decoder, and <i>that</i> call is wrapped in a total catch:
/// the decoder has been measured throwing <see cref="ArgumentException"/>, bare <see cref="Exception"/> and
/// <see cref="NullReferenceException"/> for remote-controlled input (§27.4), so a named list is a guess that
/// fails open. The catch covers the two decoder calls and nothing else; a bug in this class's own checks still
/// throws.</description></item>
/// <item><description><b>Memory is bounded, by URL, with real numbers.</b> At most
/// <see cref="DefaultMaximumEntries"/> bitmaps are retained, least recently asked-for evicted first, and every
/// retained bitmap is first scaled to <see cref="DecodeWidth"/> pixels wide (48 DIP at the 2.0 render scale the
/// row's 48x48 gutter is drawn at) with its height capped at <see cref="MaximumDecodedHeight"/>. Worst case
/// that is 256 x 96 x 384 x 4 bytes = 37.7 MB; a timeline of square avatars holds 256 x 96 x 96 x 4 = 9.4 MB.
/// Without the scale step the bound would be meaningless -- a 2 MiB PNG (the loader's byte cap) can decode to
/// tens of megabytes of pixels. The transient full-size decode is bounded by the header gate above:
/// <see cref="MaximumSourceDimension"/> squared x 4 bytes = 64 MiB at the very most, for one image at a
/// time.</description></item>
/// <item><description><b>One fetch per URL, not one per row.</b> A timeline shows the same author many times
/// over; concurrent requests for one URL share a single in-flight <see cref="Task"/>, and a completed
/// request -- including a failed one, so a dead URL is not retried once per row -- is answered from the
/// cache. A caller's cancellation only detaches that caller (<see cref="Task.WaitAsync(CancellationToken)"/>);
/// it never cancels the shared fetch another row may be waiting on.</description></item>
/// </list>
/// <para>Evicted bitmaps are not disposed: an <c>Image</c> in a row that scrolled out of view may still hold
/// one, and drawing a disposed bitmap is worse than letting the collector reclaim it.</para>
/// <para>Every method is thread-safe; the result is handed back on whatever thread the loader and the decoder
/// finished on. Marshalling to the UI thread is the caller's job (the row view does it), because this class has
/// no Avalonia dispatcher dependency and is unit-tested without one.</para>
/// </summary>
public sealed class AvatarImageCache
{
    /// <summary>Retained bitmaps, across every column. See the class remarks for what that costs.</summary>
    public const int DefaultMaximumEntries = 256;

    /// <summary>Width every retained bitmap is scaled to: the 48 DIP gutter at a 2.0 render scale.</summary>
    public const int DecodeWidth = 96;

    /// <summary>A 1:4 portrait is already not an avatar; anything taller is dropped rather than retained.</summary>
    public const int MaximumDecodedHeight = DecodeWidth * 4;

    /// <summary>Largest width or height, read from the header, that is handed to the pixel decoder at all. 4096
    /// x 4096 x 4 bytes = 64 MiB is the most one decode may allocate; a 20000 x 20000 PNG (1.5 GB) or a
    /// 65535 x 65535 GIF (16 GB) is refused without a byte of pixels being touched. Real avatars are hundreds
    /// of pixels wide, so the limit is generous without being unbounded.</summary>
    public const int MaximumSourceDimension = 4096;

    private readonly Func<IRemoteImageLoader?> _loader;
    private readonly int _maximumEntries;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new(); // Most recently used at the head.
    private readonly Dictionary<string, TaskCompletionSource<Bitmap?>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>The instance every row view uses, reading the loader from <see cref="AppServices"/> at each
    /// request so the host's assignment (or a later null) takes effect without rebuilding the views. Settable
    /// from the test assemblies only, so a render test can count calls through its own fake loader and start
    /// from an empty cache.</summary>
    public static AvatarImageCache Shared { get; internal set; } = new(() => AppServices.RemoteImageLoader);

    /// <param name="loader">Read on every request, never captured: null at request time means "no fetch", and
    /// the answer is null without touching the cache -- and without being remembered, so a loader installed
    /// later is asked.</param>
    /// <param name="maximumEntries">Retained bitmaps before the least recently used one is evicted.</param>
    public AvatarImageCache(Func<IRemoteImageLoader?> loader, int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        _loader = loader;
        _maximumEntries = maximumEntries;
    }

    /// <summary>Retained results (successful and failed alike), for tests of the bound.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Fetches still running, for tests of the in-flight sharing.</summary>
    public int InFlightCount
    {
        get
        {
            lock (_gate)
            {
                return _inFlight.Count;
            }
        }
    }

    /// <summary>The bitmap for <paramref name="url"/>, or null when there is no loader, the loader rejected or
    /// failed the fetch, or the file did not decode. Never throws for anything the remote end controls.
    /// <paramref name="cancellationToken"/> releases <i>this caller</i> only (as <see cref="OperationCanceledException"/>);
    /// the fetch itself runs to completion for whoever else asked.</summary>
    public Task<Bitmap?> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // Read per request and never cached as an answer: "no loader" is a configuration state, not a fact
        // about the URL, so a loader installed later must get the fetch rather than a remembered null.
        var loader = _loader();
        if (loader is null)
        {
            return Task.FromResult<Bitmap?>(null);
        }

        TaskCompletionSource<Bitmap?> completion;
        var started = false;
        lock (_gate)
        {
            if (_entries.TryGetValue(url, out var node))
            {
                Touch(node);
                return Task.FromResult(node.Value.Bitmap);
            }

            if (!_inFlight.TryGetValue(url, out completion!))
            {
                // Registered before the work starts, so a loader that answers synchronously cannot complete
                // before the entry exists and leave a finished task parked in the in-flight table.
                completion = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[url] = completion;
                started = true;
            }
        }

        if (started)
        {
            _ = RunAsync(url, loader, completion);
        }

        return cancellationToken.CanBeCanceled
            ? completion.Task.WaitAsync(cancellationToken)
            : completion.Task;
    }

    private async Task RunAsync(string url, IRemoteImageLoader loader, TaskCompletionSource<Bitmap?> completion)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await LoadAsync(url, loader).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Not a swallow: the exception is handed to every waiter, and the in-flight slot is still released
            // below so a transient bug does not pin the URL as "loading" forever.
            Forget(url);
            completion.SetException(ex);
            return;
        }

        lock (_gate)
        {
            _inFlight.Remove(url);
            Store(url, bitmap);
        }

        completion.SetResult(bitmap);
    }

    private static async Task<Bitmap?> LoadAsync(string url, IRemoteImageLoader loader)
    {
        // The loader's own whole-operation budget (10 s) applies; no token here, because a caller's
        // cancellation must not abort a fetch another row shares (see GetAsync).
        var path = await loader.GetOrFetchAsync(url).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        // Decoding a 2 MiB image is real CPU work; keep it off whatever thread the loader resumed on, which in
        // practice is the caller's UI thread when the disk cache answered synchronously.
        return await Task.Run(() => Decode(path)).ConfigureAwait(false);
    }

    /// <summary>Decodes <paramref name="path"/> and scales it to <see cref="DecodeWidth"/>. Null when the file
    /// cannot be read, is not an image the header reader recognises, declares dimensions over
    /// <see cref="MaximumSourceDimension"/> or outside an avatar's shape, or fails in the pixel decoder. The
    /// file came through the loader's signature check, but that check reads 12 bytes and says nothing about
    /// what the image asks the decoder to allocate (plan §27).</summary>
    internal static Bitmap? Decode(string path)
    {
        byte[] bytes;
        try
        {
            // The loader caps files at 2 MiB, so holding the bytes is cheap, and it lets the header reader and
            // the decoder each get their own stream over the same buffer (SKCodec takes ownership of the stream
            // it is given).
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!HeaderFitsAnAvatar(bytes))
        {
            return null;
        }

        return DecodePixels(bytes);
    }

    /// <summary>The gate: dimensions from the container header alone, via <see cref="SKCodec"/>, which parses
    /// the header of every container the loader accepts (PNG, JPEG, GIF, WebP) without allocating pixels and
    /// returns null -- not an exception -- for data it cannot parse. This is this class's own logic and is not
    /// wrapped: a bug here is allowed to throw.</summary>
    internal static bool HeaderFitsAnAvatar(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            return false;
        }

        var info = codec.Info;
        return FitsAnAvatar(info.Width, info.Height);
    }

    /// <summary>The header gate's rule: within <see cref="MaximumSourceDimension"/> on both sides, and not so
    /// tall that the scaled height would exceed <see cref="MaximumDecodedHeight"/> (or round to nothing).</summary>
    internal static bool FitsAnAvatar(int width, int height)
    {
        if (width < 1 || height < 1 || width > MaximumSourceDimension || height > MaximumSourceDimension)
        {
            return false;
        }

        var scaledHeight = ScaledHeight(width, height);
        return scaledHeight >= 1 && scaledHeight <= MaximumDecodedHeight;
    }

    private static int ScaledHeight(int width, int height) =>
        (int)Math.Round(height * (DecodeWidth / (double)width));

    /// <summary>The decoder boundary. Exactly two calls are wrapped -- <c>new Bitmap</c> and
    /// <c>CreateScaledBitmap</c> -- and the catch is total on purpose: the bytes were chosen by the remote end,
    /// and the decoder's failure modes for such input are not an enumerable list (plan §27.4). Everything
    /// between the two calls is this class's logic and runs outside the catch.</summary>
    private static Bitmap? DecodePixels(byte[] bytes)
    {
        Bitmap full;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            full = new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }

        using (full)
        {
            // The scale step works from what the decoder actually produced. Only the shape is re-checked here:
            // the dimension bound is the header gate's job alone, so that removing the gate is visible (a bomb
            // would come back as a bitmap) rather than silently absorbed after the allocation it exists to stop.
            var size = full.PixelSize;
            if (size.Width < 1 || size.Height < 1)
            {
                return null;
            }

            var scaledHeight = ScaledHeight(size.Width, size.Height);
            if (scaledHeight < 1 || scaledHeight > MaximumDecodedHeight)
            {
                return null;
            }

            var target = new PixelSize(DecodeWidth, scaledHeight);
            try
            {
                return full.CreateScaledBitmap(target, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private void Store(string url, Bitmap? bitmap)
    {
        var node = new LinkedListNode<Entry>(new Entry(url, bitmap));
        _recency.AddFirst(node);
        _entries[url] = node;

        while (_entries.Count > _maximumEntries)
        {
            var last = _recency.Last!;
            _recency.RemoveLast();
            _entries.Remove(last.Value.Url);
        }
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        _recency.Remove(node);
        _recency.AddFirst(node);
    }

    private void Forget(string url)
    {
        lock (_gate)
        {
            _inFlight.Remove(url);
        }
    }

    private sealed record Entry(string Url, Bitmap? Bitmap);
}
