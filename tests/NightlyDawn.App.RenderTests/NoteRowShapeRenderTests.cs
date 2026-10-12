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
/// L6c (plan §19.4/§21.3): the row's shape, measured in the real <see cref="MainWindow"/> rather than argued
/// from the XAML. Three things the fork's own row does and ours did not (plan §21.1): an avatar gutter on the
/// left, the timestamp on the right edge, and per-note command buttons that stay out of the way until the
/// pointer is over the row.
///
/// <para>Five mutations were run by hand against these three tests before this file was committed, each
/// taking exactly one of them red (failure messages quoted in the PR body): dropping the command bar's
/// resting <c>IsVisible=False</c> style so there is nothing to reveal; setting the avatar gutter's width to
/// 0; dropping the absolute timestamp's resting style; stacking the command bar under the body instead of
/// overlaying it (which reflows every row below as the pointer travels); and removing the row root's
/// <c>Background="Transparent"</c>, which silently limits the hover reveal to the rows' glyphs.</para>
/// </summary>
public sealed class NoteRowShapeRenderTests
{
    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    // The fork decodes its status avatar at 48px (StarryEyes/Views/WindowParts/Primitives/Timeline.xaml:413).
    private const double AvatarSize = 48.0;

    // A floor rather than an equality: this leaf is free to change padding *inside* a row, but it must not
    // take space away from the notes, which is exactly what PR #30 did unnoticed (plan §12).
    //
    // The numbers come from the lower CI platform, not from this machine. Measured here (macOS, 900x600,
    // scale 1.0, Keys panel collapsed): 644x220, on both master and this branch. The ubuntu leg renders the
    // same layout ~29 DIP shorter and a few DIP wider (measured against master in plan §22: 95 -> 41 at the
    // Keys-expanded size, 644 -> 653 wide), which puts Linux at roughly 191 here. 150 clears that with room
    // and still catches a regression that eats a third of the list. A floor read off the macOS 220 would be
    // red on ubuntu for a tree that never regressed -- the mistake PR #38 made in the other direction
    // (plan §22.2).
    private const double TimelineFloorWidth = 600.0;
    private const double TimelineFloorHeight = 150.0;

    private const double Epsilon = 0.5;

    [AvaloniaFact]
    public void AvatarGutterSitsLeftOfTheAuthor_AndTheTimestampIsOnTheRightEdge()
    {
        using var session = new Session();
        var window = session.Window;

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveFrame(frame!);
        AssertFrameIsNotUniform(frame!);

        var slot = session.First<Border>("AvatarSlot");
        var author = session.First<TextBlock>("AuthorLabelText");
        var time = session.First<TextBlock>("ShortTime");

        var slotBounds = session.BoundsInWindow(slot);
        var authorBounds = session.BoundsInWindow(author);
        var timeBounds = session.BoundsInWindow(time);

        Assert.True(Math.Abs(slotBounds.Width - AvatarSize) < Epsilon && Math.Abs(slotBounds.Height - AvatarSize) < Epsilon,
            $"the avatar gutter should reserve {AvatarSize}x{AvatarSize} for the image L6b will put there; measured {slotBounds.Width:0.#}x{slotBounds.Height:0.#}");

        Assert.True(slotBounds.Right <= authorBounds.X + Epsilon,
            $"the avatar gutter should sit left of the author label; gutter.Right={slotBounds.Right:0.#}, author.X={authorBounds.X:0.#}");

        Assert.True(timeBounds.X >= authorBounds.Right - Epsilon,
            $"the timestamp should be pushed to the right of the author label, not follow it; time.X={timeBounds.X:0.#}, author.Right={authorBounds.Right:0.#}");

        // Right *edge*, not merely "after the name": a DockPanel.Dock change that puts the time back beside
        // the name still satisfies the assertion above when the name is short.
        var rowBounds = session.BoundsInWindow((Visual)slot.Parent!);
        Assert.True(timeBounds.Right >= rowBounds.Right - 16.0,
            $"the timestamp should be on the row's right edge; time.Right={timeBounds.Right:0.#}, row.Right={rowBounds.Right:0.#}");

        var timeline = session.First<ListBox>("TimelineList");
        var timelineBounds = session.BoundsInWindow(timeline);
        Assert.True(timelineBounds.Width >= TimelineFloorWidth && timelineBounds.Height >= TimelineFloorHeight,
            $"TimelineList: {timelineBounds.Width:0.#}x{timelineBounds.Height:0.#} is below the floor {TimelineFloorWidth:0.#}x{TimelineFloorHeight:0.#}; the row rework took space away from the notes");
    }

    [AvaloniaFact]
    public void CommandButtons_AreHiddenAndUnhittable_UntilThePointerIsOverTheRow()
    {
        using var session = new Session();

        var reply = session.First<Button>("ReplyButton");

        Assert.False(reply.IsEffectivelyVisible,
            "the row's Reply button is visible before the pointer is anywhere near the row; the fork reveals its command panel on hover (Timeline.xaml:1286 + the :680/:683 storyboard)");

        session.HoverFirstRow();

        Assert.True(reply.IsEffectivelyVisible,
            "the row's Reply button is still hidden with the pointer over the row; hovering has to reveal it or the commands are unreachable");

        // Where Reply actually sits once revealed -- the point a user's click would land on, and the point
        // that must be harmless while the bar is hidden. Taken from the hovered state rather than from the
        // collapsed bar's own (empty) box, which resolves to somewhere in the row body and would make the
        // "unhittable" half of this test pass no matter what the bar does.
        var replyCentre = session.BoundsInWindow(reply).Center;
        var hovered = session.HitTest(replyCentre);
        Assert.True(hovered is Button || hovered?.FindAncestorOfType<Button>() is not null,
            $"the revealed Reply button is not hit-testable: a pointer at its centre lands on {hovered?.GetType().Name ?? "nothing"}");

        session.MoveAway();

        Assert.False(reply.IsEffectivelyVisible,
            "the command bar stayed visible after the pointer left the row");

        // An empty part of the row, not a glyph: the avatar gutter holds nothing until L6b wires the image in,
        // so this only reveals the bar if the row root is itself hit-testable. Without that the commands show
        // up only while the pointer is over text, which is not what the fork does and not what a user expects.
        session.HoverAvatarGutter();
        Assert.True(reply.IsEffectivelyVisible,
            "hovering the empty avatar gutter did not reveal the commands; the row's blank areas are falling through to the ListBoxItem instead of being hit-tested as part of the row");

        session.MoveAway();
        var resting = session.HitTest(replyCentre);
        Assert.True(resting is not Button && resting?.FindAncestorOfType<Button>() is null,
            $"a pointer at the hidden Reply button's position still lands on a Button ({resting?.GetType().Name}); hiding the bar has to take it out of hit testing, not just out of sight");
    }

    [AvaloniaFact]
    public void Timestamp_SwapsToTheAbsoluteFormOnHover_WithoutChangingTheRowHeight()
    {
        using var session = new Session();

        var shortTime = session.First<TextBlock>("ShortTime");
        var longTime = session.First<TextBlock>("LongTime");
        var rowHeightBefore = session.FirstRowHeight();

        Assert.True(shortTime.IsEffectivelyVisible, "the short timestamp should be the resting state");
        Assert.False(longTime.IsEffectivelyVisible, "the absolute timestamp should only appear on hover");

        session.HoverFirstRow();

        Assert.False(shortTime.IsEffectivelyVisible, "the short timestamp should give way to the absolute one on hover");
        Assert.True(longTime.IsEffectivelyVisible, "the absolute timestamp never appeared on hover");
        var expected = DateTimeOffset.FromUnixTimeSeconds(Session.FirstNoteCreatedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Assert.Equal(expected, longTime.Text); // Pins the format, not just "some other string".

        // The fork overlays its command panel; stacking it under the body instead would make every row below
        // jump as the pointer travels down the column.
        Assert.True(Math.Abs(session.FirstRowHeight() - rowHeightBefore) < Epsilon,
            $"hovering changed the row height from {rowHeightBefore:0.#} to {session.FirstRowHeight():0.#}; revealing the commands must not reflow the column");
    }

    private sealed class Session : IDisposable
    {
        public const long FirstNoteCreatedAt = 1_700_000_000 - 60;

        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-noterow-shape-render-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ProfileStore = null;
            AppServices.NotePublisher = null;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 900, Height = 600 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            var column = ((WorkspaceViewModel)Window.DataContext!).Tabs[0].Columns[0];
            column.Query = "from home"; // StaticTimelineSourceFactory ignores the text; the column still needs a query to subscribe.
            column.Subscribe();
            Pump(() => column.Notes.Count > 0, "the timeline never produced a row to measure");
            Dispatcher.UIThread.RunJobs();

            MoveAway(); // Start from a known pointer position: the window corner, not over any row.
        }

        public T First<T>(string? name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => name is null || c.Name == name)
                ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named \"{name}\" in the visual tree");

        public Rect BoundsInWindow(Visual control)
        {
            var topLeft = control.TranslatePoint(new Point(0, 0), Window) ?? new Point(0, 0);
            return new Rect(topLeft, control.Bounds.Size);
        }

        public double FirstRowHeight() => First<ListBoxItem>(null).Bounds.Height;

        public Control? HitTest(Point point) => Window.GetVisualAt(point) as Control;

        public void HoverFirstRow()
        {
            // The body text, not the command bar's own box: the reveal has to come from the row, so pointing
            // at where the buttons will appear would let a bar that is always hit-testable pass.
            var body = Window.GetVisualDescendants().OfType<TextBlock>()
                .First(tb => tb.Text is not null && tb.Text.StartsWith("A note", StringComparison.Ordinal));
            Move(BoundsInWindow(body).Center);
        }

        public void HoverAvatarGutter() => Move(BoundsInWindow(First<Border>("AvatarSlot")).Center);

        public void MoveAway() => Move(new Point(2, 2));

        private void Move(Point point)
        {
            Window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            Window.CaptureRenderedFrame(); // Forces a layout/render pass so :pointerover setters are applied.
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
            MakeNote(1, "A note long enough to wrap onto a second line so the row has a body worth measuring."),
            MakeNote(2, "A second note, so the first one is not also the last."),
        ];

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - (n * 60), // Fixed, same as the other render classes (plan §6.2).
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }

    private static void SaveFrame(WriteableBitmap frame)
    {
        Directory.CreateDirectory(OutputDirectory);
        frame.Save(Path.Combine(OutputDirectory, "noterow_shape_900x600.png"), new PngBitmapEncoderOptions());
    }

    /// <summary>Same pixel-variety check as the other render classes: proof Skia drew something.</summary>
    private static void AssertFrameIsNotUniform(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var byteCount = buffer.RowBytes * buffer.Size.Height;
        var bytes = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, byteCount);

        var distinctPixels = new HashSet<int>();
        for (var i = 0; i + 4 <= bytes.Length; i += 4 * 7)
        {
            distinctPixels.Add(BitConverter.ToInt32(bytes, i));
        }

        Assert.True(distinctPixels.Count >= 16,
            $"frame has only {distinctPixels.Count} distinct sampled pixel value(s); it looks blank rather than rendered.");
    }
}
