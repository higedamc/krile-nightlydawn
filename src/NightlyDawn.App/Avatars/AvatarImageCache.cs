using Avalonia;
using Avalonia.Media.Imaging;
using NightlyDawn.Core;

namespace NightlyDawn.App.Avatars;

/// <summary>
/// The App layer's half of the avatar pipeline (L6b, plan §26.4): turns a profile's <c>picture</c> URL into
/// an Avalonia <see cref="Bitmap"/> the row view can display, by way of <see cref="IRemoteImageLoader"/> --
/// the <b>only</b> path from a relay-supplied URL to bytes. This class never opens a socket and never looks at
/// the URL: scheme, address, size, time and container checks are all enforced on the loader's side
/// (<c>NightlyDawn.Storage.RemoteImageLoader</c>). What the loader explicitly leaves to this side, and what
/// this class therefore owns:
/// <list type="number">
/// <item><description><b>Decoding tolerates failure.</b> A file that passed the container-signature check can
/// still be corrupt or hostile, and Avalonia's decoder answers that with an exception. <see cref="Decode"/>
/// catches the types that call produces (<see cref="ArgumentException"/> from the decoder,
/// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> from opening the file) and nothing else:
/// a bug in this class is still allowed to throw.</description></item>
/// <item><description><b>Memory is bounded, by URL, with real numbers.</b> At most
/// <see cref="DefaultMaximumEntries"/> bitmaps are retained, least recently asked-for evicted first, and every
/// retained bitmap is first scaled to <see cref="DecodeWidth"/> pixels wide (48 DIP at the 2.0 render scale the
/// row's 48x48 gutter is drawn at) with its height capped at <see cref="MaximumDecodedHeight"/>. Worst case
/// that is 256 x 96 x 384 x 4 bytes = 37.7 MB; a timeline of square avatars holds 256 x 96 x 96 x 4 = 9.4 MB.
/// Without the scale step the bound would be meaningless -- a 2 MiB PNG (the loader's byte cap) can decode to
/// tens of megabytes of pixels. The full-size decode is transient and is not bounded here: reading the image
/// header before decoding is a follow-up, written down in the plan rather than half-done.</description></item>
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

    /// <summary>Decodes <paramref name="path"/> and scales it to <see cref="DecodeWidth"/>. Null on a file
    /// that does not decode (<see cref="ArgumentException"/> is what Avalonia's Skia-backed
    /// <c>ImmutableBitmap</c> throws when the decoder returns nothing), cannot be opened, or is not an
    /// avatar's shape. The file came through the loader's signature check, but that check reads 12 bytes and
    /// a corrupt or hostile image can carry a perfectly good header.</summary>
    internal static Bitmap? Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var full = new Bitmap(stream);

            var size = full.PixelSize;
            if (size.Width < 1 || size.Height < 1)
            {
                return null;
            }

            var scaledHeight = (int)Math.Round(size.Height * (DecodeWidth / (double)size.Width));
            if (scaledHeight < 1 || scaledHeight > MaximumDecodedHeight)
            {
                return null;
            }

            return full.CreateScaledBitmap(new PixelSize(DecodeWidth, scaledHeight), BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
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
