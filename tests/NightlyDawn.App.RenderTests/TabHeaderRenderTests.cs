using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L4b-3 (plan §24.6): <see cref="TimelineViewModel.ActiveTimeline"/> used to be a plain auto-property, so
/// <see cref="Views.TabStripView"/>'s <c>TabHeaderButton</c> -- bound to <c>ActiveTimeline.Title</c> -- never
/// re-evaluated after <see cref="TimelineViewModel.Subscribe"/> set it, and every tab showed the binding's
/// <c>FallbackValue</c> ("(new tab)") forever (plan §24.2). Drives the real <see cref="MainWindow"/> and asserts
/// both the before and after text: asserting only the post-subscribe value would also pass if someone merely
/// deleted the <c>FallbackValue</c> instead of actually fixing the notification.
///
/// <para>Mutation check performed by hand while writing this class (reverted after confirming red): changing
/// <see cref="TimelineViewModel.ActiveTimeline"/>'s setter back to a plain <c>private set</c> (no <c>SetField</c>)
/// turns <see cref="SubscribingATab_ChangesItsHeaderFromTheFallbackToTheQuery"/> red -- the header stays
/// "(new tab)" even though <see cref="TimelineViewModel.ActiveTimeline"/> itself is no longer null.</para>
/// </summary>
public sealed class TabHeaderRenderTests
{
    [AvaloniaFact]
    public void SubscribingATab_ChangesItsHeaderFromTheFallbackToTheQuery()
    {
        using var session = new Session();
        var tab = ((WorkspaceViewModel)session.Window.DataContext!).Columns[0].Tabs[0];

        Assert.Equal("(new tab)", session.FindTabHeaderButton(tab).Content);

        tab.Query = "from kind(\"1\")";
        tab.Subscribe();
        session.Pump();

        Assert.Equal("from kind(\"1\")", session.FindTabHeaderButton(tab).Content);
    }

    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-tab-header-render-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
        }

        public Button FindTabHeaderButton(TimelineViewModel tab) =>
            Window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == "TabHeaderButton" && ReferenceEquals(b.Tag, tab))
            ?? throw new Xunit.Sdk.XunitException("no TabHeaderButton found for the given tab");

        public void Pump() => Dispatcher.UIThread.RunJobs();

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }
    }
}
