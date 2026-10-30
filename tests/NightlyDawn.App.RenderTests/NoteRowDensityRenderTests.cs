using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L16 (plan §68.8): the fork's row density/control-template numbers, pinned against a realized row in the real
/// <see cref="MainWindow"/> rather than against the XAML source -- the L14 review (plan §66) found that a style
/// targeting a collapsed element never fires, so an assertion there is a dead vote that stays green through a
/// real regression. Every control checked below is on-screen for the row this leaf's <c>Session</c> renders
/// (<see cref="NoteRowShapeRenderTests"/> already proved the row itself renders, not blank).
///
/// <para><see cref="TextBlock.FontSize"/> resolves to the effective value once a control is attached and
/// styled (inherited values included), so reading it off the realized controls below also catches a regression
/// that removes the local setter and lets the window's own font size leak through unnoticed.</para>
/// </summary>
public sealed class NoteRowDensityRenderTests
{
    private const double Epsilon = 0.01;

    [AvaloniaFact]
    public void RowMargins_MatchTheForksMeasuredNumbers()
    {
        using var session = new Session();

        var author = session.First<TextBlock>("AuthorLabelText");
        var shortTime = session.First<TextBlock>("ShortTime");
        var longTime = session.First<TextBlock>("LongTime");

        // Timeline.xaml:1076-1088 / :1250-1285 (fork values; plan §68.8).
        Assert.True(Math.Abs(author.FontSize - 13.333) < Epsilon,
            $"screen-name FontSize should match the fork's 13.333; measured {author.FontSize}");
        Assert.True(Math.Abs(shortTime.FontSize - 10.667) < Epsilon,
            $"ShortTime FontSize should match the fork's 10.667; measured {shortTime.FontSize}");
        Assert.True(Math.Abs(longTime.FontSize - 10.667) < Epsilon,
            $"LongTime FontSize should match the fork's 10.667; measured {longTime.FontSize}");

        var avatarSlot = session.First<Border>("AvatarSlot");
        var rowRoot = (Grid)avatarSlot.Parent!;
        Assert.Equal(new Avalonia.Thickness(8, 10, 0, 10), rowRoot.Margin); // Timeline.xaml:830-832

        var nameRow = (DockPanel)author.Parent!;
        Assert.Equal(new Avalonia.Thickness(0, -2, 0, 3), nameRow.Margin); // Timeline.xaml:1070-1075

        var body = session.First<TextBlock>(null, tb => tb.Text != null && tb.Text.StartsWith("A note", StringComparison.Ordinal));
        Assert.Equal(new Avalonia.Thickness(10, 3, 2, 0), body.Margin); // Timeline.xaml:1161-1183
    }

    [AvaloniaFact]
    public void CommandButtons_MatchTheForksFavRtSize_AndEveryButtonHasSquareCorners()
    {
        using var session = new Session();

        var reply = session.First<Button>("ReplyButton");
        var repost = session.First<Button>("RepostButton");
        var quote = session.First<Button>("QuoteButton");
        var react = session.First<Button>("ReactButton");

        // Timeline.xaml:1296-1298: 32x22, widened to 46x22 for the text-carrying buttons (plan §68.8).
        foreach (var b in new[] { reply, repost, quote })
        {
            Assert.True(Math.Abs(b.Width - 46) < Epsilon, $"{b.Name} width should be 46; measured {b.Width}");
            Assert.True(Math.Abs(b.Height - 22) < Epsilon, $"{b.Name} height should be 22; measured {b.Height}");
        }

        Assert.True(Math.Abs(react.Width - 32) < Epsilon, $"ReactButton width should be 32; measured {react.Width}");
        Assert.True(Math.Abs(react.Height - 22) < Epsilon, $"ReactButton height should be 22; measured {react.Height}");

        // StarryEyes/App.xaml:32-321: every one of the fork's five button styles sets CornerRadius="0".
        // Checked on the realized buttons, not the XAML, so a future style that re-adds Fluent's default
        // rounding (even to a single Button elsewhere in the app) is caught here.
        foreach (var b in new TemplatedControl[] { reply, repost, quote, react })
        {
            Assert.Equal(new Avalonia.CornerRadius(0), b.CornerRadius);
        }
    }

    [AvaloniaFact]
    public void RowHover_AppliesTheForksLiteralBlackWash_NotATintedResourceBrush()
    {
        using var session = new Session();

        var item = session.First<ListBoxItem>(null);
        var presenter = item.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter")
            ?? throw new Xunit.Sdk.XunitException("no ContentPresenter named PART_ContentPresenter under the row's ListBoxItem");

        var body = session.First<TextBlock>(null, tb => tb.Text != null && tb.Text.StartsWith("A note", StringComparison.Ordinal));
        session.Hover(session.BoundsInWindow(body).Center);

        // Timeline.xaml:730-733: a literal 4% black wash, not a brush resource -- it has to be the same
        // color regardless of which Krile palette is active. A style setter with a literal color value
        // compiles to an ImmutableSolidColorBrush rather than a SolidColorBrush, so the check goes through
        // ISolidColorBrush, which both implement.
        var background = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
        Assert.Equal(Color.FromUInt32(0x0A000000), background.Color);
    }

    private sealed class Session : IDisposable
    {
        private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-noterow-density-render-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ProfileStore = null;
            AppServices.NotePublisher = null;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 900, Height = 600 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            var tab = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
            tab.Query = "from home"; // StaticTimelineSourceFactory ignores the text; the tab still needs a query to subscribe.
            tab.Subscribe();
            Pump(() => tab.Notes.Count > 0, "the timeline never produced a row to measure");
            Dispatcher.UIThread.RunJobs();
        }

        public T First<T>(string? name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => name is null || c.Name == name)
                ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} named \"{name}\" in the visual tree");

        public T First<T>(string? name, Func<T, bool> predicate) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => (name is null || c.Name == name) && predicate(c))
                ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} matching the predicate in the visual tree");

        public Rect BoundsInWindow(Visual control)
        {
            var topLeft = control.TranslatePoint(new Point(0, 0), Window) ?? new Point(0, 0);
            return new Rect(topLeft, control.Bounds.Size);
        }

        public void Hover(Point point)
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
            CreatedAt: 1_700_000_000 - (n * 60),
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }
}
