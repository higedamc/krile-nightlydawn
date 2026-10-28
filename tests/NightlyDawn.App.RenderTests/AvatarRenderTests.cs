using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.Avatars;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.App.Views;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L6b (plan §26.4): the avatar wiring, measured in the real <see cref="MainWindow"/> with a fake
/// <see cref="IRemoteImageLoader"/> -- not one HTTP request leaves this process. What the fake hands back is a
/// real file on disk, decoded by Avalonia's real decoder, so the pixel half of the pipeline is exercised too:
/// the last assertion of the first test reads the rendered frame at the gutter and finds the fixture's colour.
///
/// <para>Two mutations were run by hand before this file was committed, each taking exactly one test red
/// (failure text in the PR body): removing the in-flight sharing in <see cref="AvatarImageCache.GetAsync"/>
/// fails <see cref="SharedUrl_IsFetchedOnce_AndBothRowsShowIt_WithoutChangingTheRowHeight"/> (2 calls, not
/// 1); removing the <c>AppServices.RemoteImageLoader is null</c> check in <c>NoteRowView</c> fails
/// <see cref="NullLoader_FetchesNothing"/> (the fake behind the shared cache gets called).</para>
/// </summary>
public sealed class AvatarRenderTests
{
    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    private const string SharedUrl = "https://cdn.example/shared.png";
    private const string FailingUrl = "https://cdn.example/refused.png";
    private const string CorruptUrl = "https://cdn.example/corrupt.png";

    private const double Epsilon = 0.5;

    [AvaloniaFact]
    public void SharedUrl_IsFetchedOnce_AndBothRowsShowIt_WithoutChangingTheRowHeight()
    {
        using var session = new Session(
        [
            Session.Note(1, picture: SharedUrl),
            Session.Note(2, picture: SharedUrl), // a different author with the same picture: the key is the URL
            Session.Note(3, picture: null),
        ]);
        session.Loader.Gate = new TaskCompletionSource();
        session.Loader.Resolve = url => url == SharedUrl ? session.RedPng : null;
        session.Subscribe();

        // Both rows are built and both asked for the picture; the loader heard about it once.
        Assert.Equal(1, session.Loader.CallCount);
        Assert.All(session.Rows(), row => Assert.Null(row.Image.Source));
        var heightsBefore = session.RowHeights();
        Assert.Equal(3, heightsBefore.Count);
        Assert.True(heightsBefore.All(h => Math.Abs(h - heightsBefore[0]) < Epsilon),
            $"rows should be the same height before any avatar lands; measured [{string.Join(", ", heightsBefore.Select(h => h.ToString("0.#")))}]");

        session.Loader.Gate.SetResult();
        session.Pump(() => session.Rows().Count(r => r.Image.Source is not null) == 2, "the avatar never reached the two rows that named it");

        var rows = session.Rows();
        Assert.NotNull(rows[0].Image.Source);
        Assert.NotNull(rows[1].Image.Source);
        Assert.Null(rows[2].Image.Source);
        Assert.Same(rows[0].Image.Source, rows[1].Image.Source); // one bitmap, not one per row
        Assert.Equal(AvatarImageCache.DecodeWidth, ((Bitmap)rows[0].Image.Source!).PixelSize.Width); // retained scaled, not at source size
        Assert.Equal(1, session.Loader.CallCount);

        var heightsAfter = session.RowHeights();
        for (var i = 0; i < heightsBefore.Count; i++)
        {
            Assert.True(Math.Abs(heightsAfter[i] - heightsBefore[i]) < Epsilon,
                $"row {i} changed height from {heightsBefore[i]:0.#} to {heightsAfter[i]:0.#} when the avatar landed; the gutter is fixed-size so the column must not reflow");
        }

        // The pixels, not just the property: the fixture is solid red, and the gutter of a row that got it
        // has to show that colour in the rendered frame.
        var frame = session.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveFrame(frame!);
        var withImage = session.SamplePixel(frame!, session.BoundsInWindow(rows[0].Slot).Center);
        var without = session.SamplePixel(frame!, session.BoundsInWindow(rows[2].Slot).Center);
        Assert.True(withImage.R > 200 && withImage.G < 60 && withImage.B < 60,
            $"the gutter of a row with an avatar should be the fixture's red; sampled R={withImage.R} G={withImage.G} B={withImage.B}");
        Assert.False(without.R > 200 && without.G < 60 && without.B < 60,
            "the gutter of a row without an avatar is red too; the image is leaking into rows that did not ask for it");
    }

    [AvaloniaFact]
    public void NoPicture_LeavesTheSlotEmpty_AndAsksTheLoaderNothing()
    {
        using var session = new Session([Session.Note(1, picture: null), Session.Note(2, picture: "")]);
        session.Subscribe();
        session.RunJobsFor(TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, session.Loader.CallCount);
        Assert.All(session.Rows(), row => Assert.Null(row.Image.Source));
    }

    [AvaloniaFact]
    public void AFailedFetch_AndACorruptImage_LeaveTheSlotEmpty_AtTheSameRowHeight()
    {
        using var session = new Session(
        [
            Session.Note(1, picture: FailingUrl),
            Session.Note(2, picture: CorruptUrl),
            Session.Note(3, picture: null),
        ]);
        session.Loader.Resolve = url => url == CorruptUrl ? session.CorruptPng : null;
        session.Subscribe();

        // Both URLs were tried (the loader is the one that decides a fetch failed), then settled.
        session.Pump(() => AvatarImageCache.Shared.Count == 2 && AvatarImageCache.Shared.InFlightCount == 0, "the two failing avatars never settled");
        session.RunJobsFor(TimeSpan.FromMilliseconds(100)); // any late UI-thread assignment would land here

        Assert.Equal(new[] { FailingUrl, CorruptUrl }.OrderBy(u => u), session.Loader.Calls.OrderBy(u => u));
        var rows = session.Rows();
        Assert.All(rows, row => Assert.Null(row.Image.Source));

        var heights = session.RowHeights();
        Assert.True(heights.All(h => Math.Abs(h - heights[0]) < Epsilon),
            $"a failed or undecodable avatar changed a row's height; measured [{string.Join(", ", heights.Select(h => h.ToString("0.#")))}]");
    }

    [AvaloniaFact]
    public void NullLoader_FetchesNothing()
    {
        using var session = new Session([Session.Note(1, picture: SharedUrl), Session.Note(2, picture: SharedUrl)]);
        session.Loader.Resolve = _ => session.RedPng;

        // The off switch (plan §26.4). The counting fake stays behind the shared cache (see Session), so a
        // fetch that bypassed the switch would be counted.
        AppServices.RemoteImageLoader = null;
        session.Subscribe();
        session.RunJobsFor(TimeSpan.FromMilliseconds(100));

        Assert.Equal(0, session.Loader.CallCount);
        Assert.All(session.Rows(), row => Assert.Null(row.Image.Source));
    }

    [AvaloniaFact]
    public void ACacheOrLoaderBug_IsReportedOnStderr_AndDoesNotTakeTheWindowDown()
    {
        // L6b-2 (plan §27.6 item 5). GetAsync promises null for everything the remote end controls, and
        // AvatarDecodeFixtureTests measures that promise; this is the backstop for the day it is wrong. The
        // loader here throws something no catch list names. Without the backstop the exception leaves an
        // async void and ends the process -- not a red test, a dead run.
        using var session = new Session([Session.Note(1, picture: SharedUrl)]);
        session.Loader.Resolve = _ => throw new InvalidOperationException("synthetic cache bug");

        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            session.Subscribe();
            session.Pump(() => captured.ToString().Length > 0, "the backstop never wrote to stderr");
            session.RunJobsFor(TimeSpan.FromMilliseconds(100));
        }
        finally
        {
            Console.SetError(original);
        }

        var line = Assert.Single(captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("[NightlyDawn] avatar load failed (InvalidOperationException): synthetic cache bug", line);
        Assert.Null(Assert.Single(session.Rows()).Image.Source);
        Assert.Equal(0, AvatarImageCache.Shared.InFlightCount); // the URL is released, not pinned as "loading"
    }

    [AvaloniaFact]
    public void ARowThatIsGoneBeforeTheFetchCompletes_NeverReceivesTheImage()
    {
        var notes = new List<Note> { Session.Note(1, picture: SharedUrl) };
        using var session = new Session(notes);
        session.Loader.Gate = new TaskCompletionSource();
        session.Loader.Resolve = _ => session.RedPng;
        session.Subscribe();

        var view = Assert.Single(session.Rows());
        Assert.Equal(1, session.Loader.CallCount);

        // The row goes away while its fetch is still pending: resubscribing clears the column, and the source
        // now yields nothing, so the container holding this view is detached (or recycled onto no row at all).
        notes.Clear();
        session.Subscribe();
        Assert.Empty(session.Rows());

        session.Loader.Gate.SetResult();
        session.Pump(() => AvatarImageCache.Shared.Count == 1, "the shared fetch never completed");
        session.RunJobsFor(TimeSpan.FromMilliseconds(100)); // a stale continuation would assign in here

        Assert.Null(view.Image.Source);
    }

    private sealed class Session : IDisposable
    {
        private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

        private readonly string _tempDir;
        private readonly TimelineViewModel _tab;

        public MainWindow Window { get; }

        public CountingRemoteImageLoader Loader { get; } = new();

        /// <summary>An 8x8 solid red PNG, written by Avalonia's own encoder, so the decode is of a real file.</summary>
        public string RedPng { get; }

        /// <summary>The PNG signature followed by garbage: passes the loader's 12-byte container check (which
        /// is all the real loader does) and fails the header reader. Exactly the file the App side must
        /// survive; the full set of such files is AvatarDecodeFixtureTests.</summary>
        public string CorruptPng { get; }

        public Session(IReadOnlyList<Note> notes)
        {
            _tempDir = Directory.CreateTempSubdirectory("nightlydawn-avatar-render-tests").FullName;
            RedPng = WriteRedPng(Path.Combine(_tempDir, "red.png"));
            CorruptPng = Path.Combine(_tempDir, "corrupt.png");
            File.WriteAllBytes(CorruptPng, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03]);

            var profiles = new StaticProfileStore();
            foreach (var note in notes)
            {
                profiles.Seed(note.AuthorPubkey, new Profile(note.AuthorPubkey, DisplayName: "author " + note.AuthorPubkey[..4], Picture: PictureFor(note)));
            }

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(notes);
            AppServices.ProfileStore = profiles;
            AppServices.NotePublisher = null;
            AppServices.ScreenshotPath = null;
            AppServices.RemoteImageLoader = Loader;
            // The fake sits directly behind the shared cache, not behind AppServices: that is what makes
            // NullLoader_FetchesNothing a measurement. With AppServices.RemoteImageLoader null, the only thing
            // between a row and this fake is the switch in NoteRowView -- remove it, and the fake counts the call.
            AvatarImageCache.Shared = new AvatarImageCache(() => Loader);

            Window = new MainWindow { Width = 900, Height = 600 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            _tab = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
            _tab.Query = "from kind(\"1\")"; // StaticTimelineSourceFactory ignores the text; the tab still needs a query.
        }

        public void Subscribe()
        {
            _tab.Subscribe();
            // This helper is called twice by ARowThatIsGoneBeforeTheFetchCompletes..., the second time
            // expecting zero notes -- so neither `Status != IdleStatus` nor a note count works here: by
            // the second call, Status is already left over from the first Subscribe() (never reset), so
            // that AND would be true before this call's own retry even starts, degrading back to the
            // lone, unsound `!IsLoading` (Lead review, PR #50). Capturing StreamCompletion right after
            // calling Subscribe() pins *this* call's task, not the stale one from before -- IsCompleted
            // on it is false until this specific subscribe actually finishes, whichever one it is.
            var completion = _tab.StreamCompletion;
            Pump(() => completion.IsCompleted && !_tab.IsLoading, "the timeline stream never finished");
            Dispatcher.UIThread.RunJobs();
            Window.CaptureRenderedFrame(); // Realises the item containers so the row views exist and are attached.
            Dispatcher.UIThread.RunJobs();
        }

        public IReadOnlyList<(NoteRowView View, Image Image, Border Slot)> Rows() =>
            Window.GetVisualDescendants().OfType<NoteRowView>()
                .Select(view => (
                    view,
                    view.GetVisualDescendants().OfType<Image>().Single(i => i.Name == "AvatarImage"),
                    view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AvatarSlot")))
                .ToList();

        public List<double> RowHeights() =>
            Window.GetVisualDescendants().OfType<ListBoxItem>().Select(item => item.Bounds.Height).ToList();

        public Rect BoundsInWindow(Visual control)
        {
            var topLeft = control.TranslatePoint(new Point(0, 0), Window) ?? new Point(0, 0);
            return new Rect(topLeft, control.Bounds.Size);
        }

        public (byte R, byte G, byte B) SamplePixel(WriteableBitmap frame, Point point)
        {
            using var buffer = frame.Lock();
            var x = (int)point.X;
            var y = (int)point.Y;
            var bytes = new byte[4];
            System.Runtime.InteropServices.Marshal.Copy(buffer.Address + y * buffer.RowBytes + x * 4, bytes, 0, 4);
            // Headless Skia frames are Bgra8888 (the layout every other render class decodes Int32s from).
            return buffer.Format == PixelFormat.Rgba8888 ? (bytes[0], bytes[1], bytes[2]) : (bytes[2], bytes[1], bytes[0]);
        }

        /// <summary>Keeps the dispatcher turning for a while: the place a late or stale continuation would
        /// show up, if one existed. A pump with a condition cannot express "nothing happens".</summary>
        public void RunJobsFor(TimeSpan duration)
        {
            var deadline = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
        }

        public void Pump(Func<bool> condition, string timeoutMessage)
        {
            var deadline = DateTime.UtcNow + PumpTimeout;
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }

            throw new TimeoutException(timeoutMessage);
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.RemoteImageLoader = null;
            AppServices.ProfileStore = null;
            AppServices.KeyStore = null;
            AvatarImageCache.Shared = new AvatarImageCache(() => AppServices.RemoteImageLoader);
            Directory.Delete(_tempDir, recursive: true);
        }

        public static Note Note(int n, string? picture) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - n * 60, // Fixed, same as the other render classes (plan §6.2).
            Kind: NoteKind.Text,
            Content: $"note {n} ({(picture is null ? "no picture" : picture)})",
            Tags: []);

        private static string? PictureFor(Note note)
        {
            var open = note.Content.IndexOf('(');
            var inner = note.Content[(open + 1)..^1];
            return inner == "no picture" ? null : inner;
        }

        private static string WriteRedPng(string path)
        {
            using var bitmap = new WriteableBitmap(new PixelSize(8, 8), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = bitmap.Lock())
            {
                var row = new byte[buffer.RowBytes];
                for (var x = 0; x < 8; x++)
                {
                    row[x * 4 + 0] = 0x00; // B
                    row[x * 4 + 1] = 0x00; // G
                    row[x * 4 + 2] = 0xFF; // R
                    row[x * 4 + 3] = 0xFF; // A
                }

                for (var y = 0; y < 8; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
                }
            }

            bitmap.Save(path, new PngBitmapEncoderOptions());
            return path;
        }
    }

    /// <summary>Synchronous, seeded profile store (same shape as AuthorLabelRenderTests'): the row needs a
    /// Profile with a Picture at build time, nothing more.</summary>
    private sealed class StaticProfileStore : IProfileStore
    {
        private readonly Dictionary<string, Profile?> _profiles = new(StringComparer.Ordinal);

        public void Seed(string pubkey, Profile profile) => _profiles[pubkey] = profile;

        public Profile? TryGet(string pubkey) => _profiles.TryGetValue(pubkey, out var profile) ? profile : null;

        public Task PrefetchAsync(IReadOnlyCollection<string> pubkeys, CancellationToken cancellationToken = default)
        {
            foreach (var pubkey in pubkeys)
            {
                _profiles.TryAdd(pubkey, null);
            }

            return Task.CompletedTask;
        }
    }

    private static void SaveFrame(WriteableBitmap frame)
    {
        Directory.CreateDirectory(OutputDirectory);
        frame.Save(Path.Combine(OutputDirectory, "avatar_900x600.png"), new PngBitmapEncoderOptions());
    }
}
