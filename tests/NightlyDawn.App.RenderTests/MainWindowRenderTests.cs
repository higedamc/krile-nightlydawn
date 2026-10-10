using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Renders the real <see cref="MainWindow"/> through Avalonia.Headless with Skia rasterization and asserts on the
/// resulting frame, at every render scale / window size the Omarchy spike needs an answer for. No display server,
/// no network (a fake <see cref="ITimelineSourceFactory"/>), no real data directory (a temp-dir key store).
/// </summary>
public sealed class MainWindowRenderTests
{
    private const double Epsilon = 0.5; // sub-pixel layout rounding tolerance (DIP).
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    [AvaloniaTheory]
    [InlineData(1280, 800, 1.0)]
    [InlineData(1280, 800, 1.5)]
    [InlineData(1280, 800, 2.0)]
    [InlineData(900, 600, 1.0)]
    [InlineData(900, 600, 1.5)]
    [InlineData(900, 600, 2.0)]
    public void RendersALayoutThatFitsAndIsNotBlank(double width, double height, double scale)
    {
        using var session = new RenderSession(width, height, scale);
        var window = session.Window;

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveFrame(frame!, width, height, scale);

        AssertFrameIsNotUniform(frame!, scale);
        AssertFitsInsideTheWindow(window, "QueryBox");
        AssertFitsInsideTheWindow(window, "SubscribeButton");
        AssertFitsInsideTheWindow(window, "KeysExpander");
        AssertFitsInsideTheWindow(window, "TimelineList");
        AssertTextIsNotClipped(window, "Type a query, press Subscribe, and notes stream into the column below.");
    }

    /// <summary>Owns one headless window plus the fakes it was built with; loads the Keys panel (NoKey state,
    /// expanded) and a small deterministic timeline before the frame is captured.</summary>
    private sealed class RenderSession : IDisposable
    {
        public const string FirstNoteContent = "Landed in Omarchy; Hyprland + Wayland feels snappy so far.";

        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public RenderSession(double width, double height, double scale)
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-render-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = width, Height = height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, scale);
            Dispatcher.UIThread.RunJobs();

            var keysExpander = Window.FindControl<Expander>("KeysExpander")!;
            keysExpander.IsExpanded = true;
            Pump(() => Window.FindControl<Control>("GenerateBox")?.IsVisible == true,
                "the Keys panel never reached the NoKey/Generate state");

            var timeline = (TimelineColumnViewModel)Window.DataContext!;
            timeline.Query = "from home";
            timeline.Subscribe();
            Pump(() => !timeline.IsLoading, "the timeline stream never finished");
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private static void Pump(Func<bool> condition, string timeoutMessage)
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

        private static IReadOnlyList<Note> SampleNotes() =>
        [
            MakeNote(1, FirstNoteContent),
            MakeNote(2, "Checking whether the IME preedit survives a relay round trip."),
            MakeNote(3, "[render-test] a third note so the list has some scroll height."),
        ];

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - n * 60,
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }

    private static void SaveFrame(WriteableBitmap frame, double width, double height, double scale)
    {
        Directory.CreateDirectory(OutputDirectory);
        var path = Path.Combine(OutputDirectory, $"mainwindow_{width:0}x{height:0}_scale{scale:0.0}.png");
        frame.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>(a): a frame with one color is either a blank canvas or a mock renderer — proof that Skia actually
    /// drew the window, not just that a bitmap of the right size exists.</summary>
    private static void AssertFrameIsNotUniform(WriteableBitmap frame, double scale)
    {
        using var buffer = frame.Lock();
        var byteCount = buffer.RowBytes * buffer.Size.Height;
        var bytes = new byte[byteCount];
        Marshal.Copy(buffer.Address, bytes, 0, byteCount);

        var distinctPixels = new HashSet<int>();
        for (var i = 0; i + 4 <= bytes.Length; i += 4 * 7) // stride-sample; this is a variety check, not a diff.
        {
            distinctPixels.Add(BitConverter.ToInt32(bytes, i));
        }

        Assert.True(distinctPixels.Count >= 16,
            $"frame at scale {scale:0.0} has only {distinctPixels.Count} distinct sampled pixel value(s) " +
            $"({buffer.Size.Width}x{buffer.Size.Height}); it looks blank rather than rendered.");
    }

    /// <summary>(b): the named control must be laid out entirely inside the window's DIP bounds, with a non-zero
    /// size — catches the "control clipped/overlaps at this size" class of HiDPI/layout break.</summary>
    private static void AssertFitsInsideTheWindow(MainWindow window, string controlName)
    {
        var control = window.FindControl<Control>(controlName) ?? throw new Xunit.Sdk.XunitException($"{controlName}: not found in the visual tree");
        var topLeft = control.TranslatePoint(new Point(0, 0), window) ?? throw new Xunit.Sdk.XunitException($"{controlName}: not attached to the window's visual tree");
        var bounds = control.Bounds;

        Assert.True(bounds.Width > 0 && bounds.Height > 0,
            $"{controlName}: zero size ({bounds.Width:0.#}x{bounds.Height:0.#})");
        Assert.True(topLeft.X >= -Epsilon && topLeft.Y >= -Epsilon,
            $"{controlName}: starts outside the window at ({topLeft.X:0.#},{topLeft.Y:0.#})");
        Assert.True(topLeft.X + bounds.Width <= window.Bounds.Width + Epsilon && topLeft.Y + bounds.Height <= window.Bounds.Height + Epsilon,
            $"{controlName}: overflows the window — bottom-right ({topLeft.X + bounds.Width:0.#},{topLeft.Y + bounds.Height:0.#}) vs window ({window.Bounds.Width:0.#},{window.Bounds.Height:0.#})");
    }

    /// <summary>(c): the width the text actually needs must fit in the space available where it sits. A laid-out
    /// TextBlock's own <c>DesiredSize</c> is useless for this: Avalonia measures single-line text to the width its
    /// parent handed it, not its natural width, so it is never larger than what it was given and a direct
    /// Desired-vs-Bounds comparison can never fail. Measuring an unattached clone with the same font settings at
    /// an unconstrained width gives the text's true (un-clipped) extent to compare against the real budget.</summary>
    private static void AssertTextIsNotClipped(MainWindow window, string text)
    {
        var textBlock = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().FirstOrDefault(tb => tb.Text == text)
            ?? throw new Xunit.Sdk.XunitException($"TextBlock with text \"{text}\" not found in the visual tree");
        var topLeft = textBlock.TranslatePoint(new Point(0, 0), window) ?? throw new Xunit.Sdk.XunitException("text block not attached to the window's visual tree");

        var probe = new Avalonia.Controls.TextBlock
        {
            Text = textBlock.Text,
            FontFamily = textBlock.FontFamily,
            FontSize = textBlock.FontSize,
            FontWeight = textBlock.FontWeight,
        };
        probe.Measure(Size.Infinity);
        var naturalWidth = probe.DesiredSize.Width;
        var availableWidth = window.Bounds.Width - topLeft.X;

        Assert.True(naturalWidth <= availableWidth + Epsilon,
            $"text needs {naturalWidth:0.#} but only {availableWidth:0.#} is available before the window edge (\"{text}\")");
    }
}
