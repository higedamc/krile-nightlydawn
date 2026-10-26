using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Plan §48-49 / L10 v2 (Lead's review of #49): the hint's own length used to decide the settings panel's
/// width, because <c>QueryBox</c> sits inside a <see cref="Popup"/> where the available width is unconstrained,
/// so the <c>*</c> column grows to fit <c>PlaceholderText</c>'s rendered ink. At the fork's original
/// 146-character, three-example hint that pushed the panel to 1138 DIP wide -- enough that a 1280 DIP window
/// left only 142 DIP of margin, and a 1100 DIP window pushed <c>SubscribeButton</c> fully outside the window's
/// own bounds. Lead's probe found that past that point, the headless input path's coordinates and
/// <c>TranslatePoint</c>'s coordinates disagree: clicking the button's own reported centre no longer sets
/// <c>IsPointerOver</c>, so the click never reaches <c>OnSubscribeClick</c> at all -- not a visual overflow, a
/// silently dead button.
///
/// <para>This is the receiving test for L10 v2's fix (<c>TabStripView.axaml</c>'s <c>PlaceholderText</c> now
/// carries one short example; the full three live in <c>ToolTip.Tip</c>, which does not participate in its
/// owner's measure pass). It drives the real button through a real <c>MouseMove</c> then <c>MouseDown</c>/
/// <c>MouseUp</c> pair -- not <see cref="WindowChromeRenderTests.Session.Click"/>'s shortcut of going straight
/// to <c>MouseDown</c>/<c>MouseUp</c> at the reported centre, since that would miss exactly the coordinate
/// mismatch this test exists to catch -- and asserts on the side that actually matters: the subscription
/// started and the fixture notes arrived, not a DIP measurement.</para>
///
/// <para>1280 is CI's window size; 1100 is the one that actually failed before the fix (1280 kept 142 DIP of
/// margin even with the old hint).</para>
///
/// <para>Mutation (a), run by hand and reverted after confirming red (<c>cmp</c> confirmed byte-identical
/// restore): swapping the fix's short <c>PlaceholderText</c> back for the fork's original 146-character,
/// three-example string turns <see cref="SubscribeButton_IsReachable"/> red at window width 1100 --
/// <c>SubscribeButton.IsPointerOver</c> stays <c>false</c> after the <c>MouseMove</c>, and the subsequent
/// click never starts the subscription (<c>tab.Notes</c> stays empty). Width 1280 still passed under the same
/// mutation, matching Lead's measured 142 DIP margin -- which is exactly why this test must include 1100, not
/// just CI's own window size.</para>
/// </summary>
public sealed class QueryHintLayoutRenderTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    [AvaloniaTheory]
    [InlineData(1280.0)]
    [InlineData(1100.0)]
    public void SubscribeButton_IsReachable(double windowWidth)
    {
        using var session = new Session(windowWidth);

        var settingsToggle = session.Find<ToggleButton>("SettingsToggle") ?? throw new Xunit.Sdk.XunitException("SettingsToggle not found");
        session.Click(settingsToggle);

        var queryBox = session.Find<TextBox>("QueryBox") ?? throw new Xunit.Sdk.XunitException("QueryBox not found after opening the settings toggle");
        var subscribeButton = session.Find<Button>("SubscribeButton") ?? throw new Xunit.Sdk.XunitException("SubscribeButton not found");

        queryBox.Text = "from kind(\"1\") where ()";

        var center = subscribeButton.TranslatePoint(new Point(subscribeButton.Bounds.Width / 2, subscribeButton.Bounds.Height / 2), session.Window)
            ?? throw new Xunit.Sdk.XunitException("SubscribeButton: not attached to the window's visual tree");
        session.Window.MouseMove(center);
        session.Pump();
        Assert.True(subscribeButton.IsPointerOver, $"SubscribeButton.IsPointerOver should be true after MouseMove at its own reported centre (window width {windowWidth})");

        session.Window.MouseDown(center, MouseButton.Left);
        session.Pump();
        session.Window.MouseUp(center, MouseButton.Left);
        session.Pump();

        var tab = ((WorkspaceViewModel)session.Window.DataContext!).Columns[0].Tabs[0];
        session.Pump(() => tab.StreamCompletion.IsCompleted && tab.Notes.Count == 2, $"subscribing through SubscribeButton never completed (window width {windowWidth})");

        Assert.Equal(["first", "second"], tab.Notes.Select(n => n.DisplayContent));
    }

    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session(double width)
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-query-hint-layout-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = width, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Pump();
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public void Click(Control control)
        {
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
                ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached to the window's visual tree");
            Window.MouseDown(center, MouseButton.Left);
            Pump();
            Window.MouseUp(center, MouseButton.Left);
            Pump();
        }

        public void Pump() => Dispatcher.UIThread.RunJobs();

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
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private static IReadOnlyList<Note> SampleNotes() =>
        [
            MakeNote(1, "first"),
            MakeNote(2, "second"),
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
