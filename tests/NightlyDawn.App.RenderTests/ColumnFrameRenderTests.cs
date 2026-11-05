using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.App.Views;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L19 (plan §70): a column draws no frame. The fork's column root is <c>Grid Margin="8,4,8,0" Background="{x:Null}"</c>
/// (StarryEyes <c>MainArea.xaml:84</c>) and its timeline root is a bare <c>DockPanel</c> (<c>Timeline.xaml:1985</c>);
/// columns are separated by margin alone. Everything here reads the realised visual tree and its bounds, not the XAML text.
///
/// <para>Negative controls run while writing this (each reverted afterwards, application counts printed): restoring
/// <c>Border Padding="12" BorderThickness="1" CornerRadius="6"</c> around <c>TimelineView</c>'s content turns
/// <see cref="NothingBetweenTheColumnRootAndTheListDrawsAFrame"/> red; putting the <c>0,4,0,0</c> margin on both the
/// host and <c>TimelineView</c> turns <see cref="TheFork_Geometry_IsPlacedOnce_AndTheGutterIsSixteen"/> red (8, not 4);
/// dropping <c>ColumnRoot</c>'s <c>Margin</c> turns the same test red (gutter 0).</para>
/// </summary>
public sealed class ColumnFrameRenderTests
{
    [AvaloniaFact]
    public void NothingBetweenTheColumnRootAndTheListDrawsAFrame()
    {
        using var s = new Session();
        var list = s.List(0);
        var columnRoot = s.ColumnRoots()[0];

        var between = list.GetVisualAncestors().TakeWhile(a => !ReferenceEquals(a, columnRoot)).ToList();
        Assert.True(between.Count > 0 && list.GetVisualAncestors().Contains(columnRoot), "precondition: the list is inside the column root");

        foreach (var border in between.OfType<Border>())
        {
            Assert.Equal(new Thickness(0), border.BorderThickness);
            Assert.Equal(new CornerRadius(0), border.CornerRadius);
            Assert.Equal(new Thickness(0), border.Padding);
        }

        // And nothing stands in for the frame: no tint, no line, no shadow on anything between (the original has none).
        foreach (var visual in between)
        {
            if (visual is Border { BorderBrush: not null } withBrush)
            {
                Assert.Equal(new Thickness(0), withBrush.BorderThickness);
            }

            Assert.True(IsClear((visual as Panel)?.Background), $"{visual.GetType().Name} between the column root and the list paints a background");
            Assert.True(IsClear((visual as Border)?.Background), $"{visual.GetType().Name} between the column root and the list paints a background");
            Assert.Equal(0, (visual as Border)?.BoxShadow.Count ?? 0);
        }

        Assert.True(IsClear(columnRoot.Background), "ColumnRoot paints no background (the fork's Background={x:Null})");
    }

    [AvaloniaFact]
    public void TheFork_Geometry_IsPlacedOnce_AndTheGutterIsSixteen()
    {
        using var s = new Session();
        s.Workspace.AddColumn();
        s.Pump();

        var roots = s.ColumnRoots();
        Assert.Equal(2, roots.Count);

        foreach (var root in roots)
        {
            Assert.Equal(new Thickness(8, 4, 8, 0), root.Margin);
        }

        // The gutter between two neighbours is each column's own 8 + 8, measured between realised bounds.
        var left = s.BoundsInWindow(roots[0]);
        var right = s.BoundsInWindow(roots[1]);
        Assert.Equal(16, right.X - left.Right, precision: 1);

        // The 4 DIP between the tab row and the timeline is placed once: measured from the bottom of the tab strip to the
        // top of the timeline, not read off a Margin (a margin on both the host and TimelineView would make it 8).
        // The timeline's own content root (TimelineView's first DockPanel), not the UserControl: a margin on that root sits
        // inside the UserControl's Bounds and would be invisible to a measurement of the UserControl itself.
        var tabStrip = s.BoundsInWindow(s.TabStripViews()[0]);
        var timeline = s.BoundsInWindow(s.TimelineViews()[0].GetVisualDescendants().OfType<DockPanel>().First());
        Assert.Equal(4, timeline.Y - tabStrip.Bottom, precision: 1);
    }

    private static bool IsClear(IBrush? brush) => brush is null || brush is ISolidColorBrush { Color.A: 0 };

    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }
        public WorkspaceViewModel Workspace { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-column-frame-render-tests").FullName;
            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
            Workspace = (WorkspaceViewModel)Window.DataContext!;
        }

        public void Pump() => Dispatcher.UIThread.RunJobs();

        public List<DockPanel> ColumnRoots() =>
            Window.GetVisualDescendants().OfType<DockPanel>().Where(d => d.Name == "ColumnRoot")
                .OrderBy(d => BoundsInWindow(d).X).ToList();

        public ListBox List(int column) =>
            ColumnRoots()[column].GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "TimelineList");

        public List<TabStripView> TabStripViews() =>
            Window.GetVisualDescendants().OfType<TabStripView>().OrderBy(v => BoundsInWindow(v).X).ToList();

        public List<TimelineView> TimelineViews() =>
            Window.GetVisualDescendants().OfType<TimelineView>().OrderBy(v => BoundsInWindow(v).X).ToList();

        public Rect BoundsInWindow(Visual control)
        {
            var topLeft = control.TranslatePoint(new Point(0, 0), Window)
                ?? throw new Xunit.Sdk.XunitException($"{control.GetType().Name}: not attached to the window");
            return new Rect(topLeft, control.Bounds.Size);
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }
    }
}
