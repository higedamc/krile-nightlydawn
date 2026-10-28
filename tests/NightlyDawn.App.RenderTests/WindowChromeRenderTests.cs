using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Plan §26.5 (L4c ②④⑥) and §28.6's L4c-2 follow-up, end to end through the real <see cref="MainWindow"/>:
/// the compose box is the window's top row (②), a column's head shows only its tab strip until the gear/AddTab
/// opens its settings (④, now a <see cref="Popup"/> per Lead's §28 review so an open panel can never change
/// <c>ColumnsHost</c>'s width), the gear's own checked-paint and the panel's open state share one source of
/// truth (§28.6 ①), a sole column stretches to fill the window instead of sitting at its bare minimum (§28.6
/// ③), and the status line reports the selected tab's note count (⑥).
///
/// <para>All clicks below go through <see cref="Session.Click"/>'s real <c>MouseDown</c>/<c>MouseUp</c>
/// pair, not <c>RaiseEvent(Button.ClickEvent)</c>: Lead's §28 review found that raising the event directly
/// skips <c>ToggleButton.OnClick()</c> entirely, so <c>SettingsToggle.IsChecked</c> never moved and the five
/// tests that predated this file's §28.6 pass could not have caught the gear/panel desync they were meant to
/// guard.</para>
///
/// <para>Mutation checks performed by hand while writing this class (all reverted after confirming red):
/// (1) removing <c>TabStripView.axaml.cs</c>'s <c>_settingsToggle.IsChecked = true;</c> from <c>OnAddTabClick</c>
/// turns <see cref="AddingATab_OpensItsSettingsImmediately"/> red (a brand-new, unsubscribed tab's QueryBox
/// stays collapsed, the "死んだUI" plan §26.5 ④ rules out). (2) changing <c>MainWindow.axaml</c>'s
/// <c>StatusLine.Text</c> binding to a literal string turns
/// <see cref="StatusLine_ChangesFromTheFallbackToARealCountAfterSubscribing"/> red on the post-subscribe half
/// only -- confirming the fallback-only half alone would not have caught plan §24.2's class of bug.
/// (3) hard-coding <c>WorkspaceViewModel.RecalculateColumnWidths</c> to always use <c>SoleColumnMinWidth</c>
/// turns <see cref="ThreeColumns_FitInsideA1280WidthWindow"/> red (the measured total overflows 1280).
/// (4) reverting <c>TabStripView.axaml</c>'s <c>Popup.IsOpen</c> binding back to a second,
/// hand-toggled <c>IsVisible</c> flag (restoring the pre-§28.6 <c>_settingsPanel.IsVisible = !_settingsPanel.IsVisible</c>
/// click handler) turns <see cref="SettingsToggle_CheckedStateAndPanelOpenStateNeverDisagree"/> red at the
/// step right after <c>AddTabButton</c> is pressed: the panel opens (the handler nobody removed still does
/// that), but nothing tells the gear, so its checked-paint and the panel's open state disagree exactly the
/// way Lead's §28 review measured by hand. (5) reverting <c>WorkspaceView.axaml</c>'s <c>ItemsPanelTemplate</c>
/// from <c>UniformGrid Rows="1"</c> back to a horizontal <c>StackPanel</c> turns
/// <see cref="SoleColumn_FillsTheAvailableWidth"/> red (<c>ColumnRoot.Bounds.Width</c> drops to 640, its bare
/// <c>MinWidthHint</c>, instead of the viewport's 1157) -- confirming Lead's §30 review that the test's prior
/// version, which asserted against <c>ColumnsHost</c> instead, could not have caught this: <c>ColumnsHost</c>
/// stays the full viewport width either way because that width comes from its own <c>MinWidth</c> binding, not
/// from how its <c>ItemsPanel</c> arranges the column inside it.</para>
/// </summary>
public sealed class WindowChromeRenderTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>② -- the compose box must be above <c>Workspace</c>, and nothing with the old heading's
    /// text sits above it (plan §26.5: "最上段へ"). A row re-inserted above it would pass a bare
    /// "above Workspace" check but fail this one.</summary>
    [AvaloniaFact]
    public void ComposeBoxIsTheWindowsTopRow()
    {
        using var session = new Session();
        var window = session.Window;

        var composeBox = session.Find<Control>("ComposeBox") ?? throw new Xunit.Sdk.XunitException("ComposeBox not found");
        var workspace = session.Find<Control>("Workspace") ?? throw new Xunit.Sdk.XunitException("Workspace not found");

        var composeTop = session.BoundsInWindow(composeBox).Y;
        var workspaceTop = session.BoundsInWindow(workspace).Y;

        Assert.True(composeTop < workspaceTop, $"ComposeBox.Y={composeTop:0.#} should sit above Workspace.Y={workspaceTop:0.#}");

        // The window's own 16 DIP outer margin (MainWindow.axaml) is the only thing allowed above it.
        Assert.True(composeTop <= 16.5, $"ComposeBox.Y={composeTop:0.#} is not the window's top row");
    }

    /// <summary>④ -- a column's head shows only the tab strip by default; QueryBox/SubscribeButton are
    /// unreachable until the gear opens them (plan §26.5: "死んだUIを置かない"), and once opened they render
    /// and actually work (this proves the opened state is not just visible but usable). Since §28.6 ②'s fix
    /// moved the panel into a <see cref="Popup"/>, "collapsed by default" means the Popup's <c>Child</c> is
    /// not realized at all before the first open (confirmed by hand: <c>GetVisualDescendants()</c> returns
    /// nothing named <c>QueryBox</c> pre-open), not merely <c>IsEffectivelyVisible == false</c> on an
    /// existing control -- a stronger property than the pre-Popup design had, not a weaker one.</summary>
    [AvaloniaFact]
    public void ColumnHead_HidesQueryBoxUntilSettingsAreOpened_ThenItWorks()
    {
        using var session = new Session();
        var window = session.Window;

        Assert.Null(session.Find<TextBox>("QueryBox"));

        var settingsToggle = session.Find<ToggleButton>("SettingsToggle") ?? throw new Xunit.Sdk.XunitException("SettingsToggle not found");
        session.Click(settingsToggle);

        var queryBox = session.Find<TextBox>("QueryBox") ?? throw new Xunit.Sdk.XunitException("QueryBox not found after opening the settings toggle");
        Assert.True(queryBox.IsEffectivelyVisible, "QueryBox should be visible once the settings toggle is pressed");
        Assert.True(queryBox.Bounds.Width > 0 && queryBox.Bounds.Height > 0, $"QueryBox: zero size ({queryBox.Bounds.Width:0.#}x{queryBox.Bounds.Height:0.#})");

        var subscribeButton = session.Find<Button>("SubscribeButton") ?? throw new Xunit.Sdk.XunitException("SubscribeButton not found");
        var tab = ((WorkspaceViewModel)window.DataContext!).Columns[0].Tabs[0];

        queryBox.Text = "from kind(\"1\")";
        session.Click(subscribeButton);
        // `!X.IsLoading` alone cannot tell "never subscribed" from "finished": IsLoading defaults to
        // false, the same value it holds once a subscribe completes, so a tab whose click never landed
        // would pass this wait instantly too (plan §46/§48/§50; WaitSoundnessTests demonstrates it).
        // ANDing with `Status != IdleStatus` closes the gap -- IdleStatus is the one value Subscribe()
        // never leaves the tab in.
        session.Pump(() => !tab.IsLoading && tab.Status != TimelineViewModel.IdleStatus, "subscribing through the opened QueryBox/SubscribeButton never finished");

        Assert.Equal(["first", "second"], tab.Notes.Select(n => n.DisplayContent));
    }

    /// <summary>§28.6 ① -- the gear's own checked-paint (<c>SettingsToggle.IsChecked</c>) and the settings
    /// panel's open state (<c>Popup.IsOpen</c>) must agree after every real pointer transition, not just the
    /// one Lead's review happened to click through by hand. <c>RaiseEvent(Button.ClickEvent)</c> would not
    /// have caught the original bug (it skips <c>ToggleButton.OnClick()</c>, so <c>IsChecked</c> never moves),
    /// which is why every step here goes through <see cref="Session.Click"/>'s real pointer input instead.</summary>
    [AvaloniaFact]
    public void SettingsToggle_CheckedStateAndPanelOpenStateNeverDisagree()
    {
        using var session = new Session();
        var window = session.Window;

        var settingsToggle = session.Find<ToggleButton>("SettingsToggle") ?? throw new Xunit.Sdk.XunitException("SettingsToggle not found");
        var panel = session.Find<Popup>("TabSettingsPanel") ?? throw new Xunit.Sdk.XunitException("TabSettingsPanel not found");

        AssertAgree("initial", open: false);

        session.Click(settingsToggle);
        AssertAgree("after clicking the gear once", open: true);

        session.Click(settingsToggle);
        AssertAgree("after clicking the gear a second time", open: false);

        // AddTab opens the new tab's settings programmatically (plan §26.5 ④), the one path that is not a
        // direct click on the gear -- this is exactly the step that desynced before §28.6 ① (the gear stayed
        // unchecked while the panel underneath it was open).
        var addTabButton = session.Find<Button>("AddTabButton") ?? throw new Xunit.Sdk.XunitException("AddTabButton not found");
        session.Click(addTabButton);
        AssertAgree("right after AddTab (programmatic open, not a gear click)", open: true);

        session.Click(settingsToggle);
        AssertAgree("after clicking the gear once more to close the AddTab-opened panel", open: false);

        void AssertAgree(string when, bool open)
        {
            Assert.Equal(open, settingsToggle.IsChecked == true);
            Assert.Equal(open, panel.IsOpen);
            Assert.Equal(settingsToggle.IsChecked == true, panel.IsOpen);
        }
    }

    /// <summary>④ -- a freshly added tab is unsubscribed, so its settings must already be open (plan §26.5:
    /// "+ Tab の直後にその設定が開く"); otherwise a new tab would look like dead UI until someone found the
    /// gear by accident.</summary>
    [AvaloniaFact]
    public void AddingATab_OpensItsSettingsImmediately()
    {
        using var session = new Session();
        var window = session.Window;

        Assert.Null(session.Find<TextBox>("QueryBox")); // sanity check: unreachable before AddTab is pressed (§28.6 ②'s Popup).

        var addTabButton = session.Find<Button>("AddTabButton") ?? throw new Xunit.Sdk.XunitException("AddTabButton not found");
        session.Click(addTabButton);

        var queryBoxAfter = session.Find<TextBox>("QueryBox") ?? throw new Xunit.Sdk.XunitException("QueryBox not found after AddTab");
        Assert.True(queryBoxAfter.IsEffectivelyVisible, "the new tab's settings should already be open, with no extra click needed");
    }

    /// <summary>§28.6 ② -- the settings panel is a <see cref="Popup"/>, not an inline row sharing the
    /// column's own layout (Lead's §28 review: opening it used to force the column wider, overflowing the
    /// window's 1280 DIP total with all three gears open). Measured here directly rather than inferred from
    /// the mechanism: <c>ColumnsHost</c>'s width must be identical whether the panel is open or closed.</summary>
    [AvaloniaFact]
    public void OpeningTheSettingsPanelNeverChangesColumnsHostWidth()
    {
        using var session = new Session();
        var window = session.Window;
        var workspace = (WorkspaceViewModel)window.DataContext!;

        workspace.AddColumn();
        workspace.AddColumn();
        session.Pump();

        var columnsHost = session.Find<ItemsControl>("ColumnsHost") ?? throw new Xunit.Sdk.XunitException("ColumnsHost not found");
        var widthBeforeOpen = columnsHost.Bounds.Width;

        foreach (var settingsToggle in session.FindAll<ToggleButton>("SettingsToggle"))
        {
            session.Click(settingsToggle);
        }

        var widthAllOpen = columnsHost.Bounds.Width;
        Assert.Equal(widthBeforeOpen, widthAllOpen, precision: 1);
    }

    /// <summary>§28.6 ③ -- a sole column must fill the window, not sit at its bare <c>MinWidthHint</c>
    /// (Lead's §28 review, dimension ②: master's single column used 640 of the 1248 DIP available, 608 DIP
    /// left blank). "Fills" is measured against <c>ColumnsScrollViewer</c>'s own live viewport, not a
    /// literal 1248 -- that constant turned out to assume <c>AddColumnButton</c> costs zero width, which is
    /// false (measured: <c>AddColumnButton</c> + its margin is 91 DIP, so the real viewport a sole column has
    /// to fill is 1157, not 1248).
    ///
    /// <para>Lead's §30 review found the first version of this test vacuous: it asserted against
    /// <c>ColumnsHost</c>'s own <c>Bounds.Width</c>, but that width is forced wide by <c>ColumnsHost.MinWidth</c>
    /// itself (bound to <c>ColumnsScrollViewer.Bounds.Width</c>) regardless of how its <c>ItemsPanel</c> lays
    /// out the column inside -- reverting <c>UniformGrid Rows="1"</c> to the pre-§28.6 horizontal
    /// <c>StackPanel</c> left all 70 tests green because <c>ColumnsHost</c> stayed exactly as wide either way.
    /// This asserts against the column's own root (<c>ColumnRoot</c>, the per-column <c>DockPanel</c> named in
    /// <c>WorkspaceView.axaml</c>) instead: a <c>StackPanel</c> sizes a horizontal child to its own desired
    /// width (<c>MinWidthHint</c>, 640) regardless of how much room its parent has, so only the real stretch
    /// mechanism -- <c>UniformGrid</c> handing each cell an equal share of a finite, bound-in <c>MinWidth</c> --
    /// can make <c>ColumnRoot</c> itself report the full viewport.</para>
    ///
    /// The floor comparison is what proves this is genuine stretching and not a coincidence: 1157 has to be
    /// well above <c>SoleColumnMinWidth</c> (640) for the equality above to mean anything.</summary>
    [AvaloniaFact]
    public void SoleColumn_FillsTheAvailableWidth()
    {
        using var session = new Session();
        var window = session.Window;

        var scrollViewer = session.Find<ScrollViewer>("ColumnsScrollViewer") ?? throw new Xunit.Sdk.XunitException("ColumnsScrollViewer not found");
        var columnRoot = session.Find<DockPanel>("ColumnRoot") ?? throw new Xunit.Sdk.XunitException("ColumnRoot not found");

        Assert.Equal(scrollViewer.Bounds.Width, columnRoot.Bounds.Width, precision: 1);
        Assert.True(columnRoot.Bounds.Width > 1000,
            $"ColumnRoot: {columnRoot.Bounds.Width:0.#} is not meaningfully past SoleColumnMinWidth (640) -- " +
            "the equality above could pass by coincidence if the viewport itself were small");
    }

    /// <summary>⑥ -- the status line changes from the fallback to a real count once the selected tab
    /// subscribes, not just that it holds some plausible-looking text (plan §24.2's class of bug: a
    /// FallbackValue alone would make the before/after states indistinguishable).</summary>
    [AvaloniaFact]
    public void StatusLine_ChangesFromTheFallbackToARealCountAfterSubscribing()
    {
        using var session = new Session();
        var window = session.Window;

        var statusLine = session.Find<TextBlock>("StatusLine") ?? throw new Xunit.Sdk.XunitException("StatusLine not found");
        Assert.Equal("0 notes", statusLine.Text);

        var tab = ((WorkspaceViewModel)window.DataContext!).Columns[0].Tabs[0];
        tab.Query = "from kind(\"1\")";
        tab.Subscribe();
        session.Pump(() => !tab.IsLoading && tab.Status != TimelineViewModel.IdleStatus, "the timeline stream never finished");

        Assert.Equal("2 notes", statusLine.Text);
    }

    /// <summary>横 -- plan §26.5's hard width receipt: 3 columns opened through the real <c>+ Column</c>
    /// button must fit inside the window's real viewport with no horizontal scrolling needed, down from
    /// master `c38cd5d2`'s measured 2,205 DIP (1 column's floor of 727 DIP × 3; plan §25.3). The bound
    /// compared against is <c>ColumnsScrollViewer</c>'s own live width, not a literal 1248 -- that constant
    /// silently assumed <c>AddColumnButton</c> (which sits outside the scroll area, §28.6 ③) costs zero
    /// width, which measured false by 91 DIP (see <see cref="SoleColumn_FillsTheAvailableWidth"/>'s doc).
    /// Reported here, not assumed -- the number this asserts against is the one this leaf's own macOS run
    /// measured; the PR body carries the Linux CI number alongside it per plan §26.5's instruction.</summary>
    [AvaloniaFact]
    public void ThreeColumns_FitInsideA1280WidthWindow()
    {
        using var session = new Session();
        var window = session.Window;
        var workspace = (WorkspaceViewModel)window.DataContext!;

        workspace.AddColumn();
        workspace.AddColumn();
        session.Pump();

        var scrollViewer = session.Find<ScrollViewer>("ColumnsScrollViewer") ?? throw new Xunit.Sdk.XunitException("ColumnsScrollViewer not found");
        var columnsHost = session.Find<ItemsControl>("ColumnsHost") ?? throw new Xunit.Sdk.XunitException("ColumnsHost not found");
        var width = columnsHost.Bounds.Width;
        var availableWidth = scrollViewer.Bounds.Width;

        Assert.True(width <= availableWidth + 0.5,
            $"ColumnsHost: {width:0.#} overflows the {availableWidth:0.#} DIP available in ColumnsScrollViewer (3 columns)");
    }

    /// <summary>Owns one headless <see cref="MainWindow"/>, same composition pattern as
    /// <see cref="ColumnTabIndependenceRenderTests"/>'s Session -- a <see cref="LocalKeyStore"/> (not null)
    /// so <c>from kind(...)</c> subscribes resolve the same way a real run's Keys panel would.</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-window-chrome-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public IReadOnlyList<T> FindAll<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

        public Point BoundsInWindow(Control control) =>
            control.TranslatePoint(new Point(0, 0), Window) ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached to the window's visual tree");

        /// <summary>Real <c>MouseDown</c>/<c>MouseUp</c> at the control's own centre, not
        /// <c>RaiseEvent(Button.ClickEvent)</c>: a <see cref="ToggleButton"/> only flips <c>IsChecked</c>
        /// inside its pointer-driven <c>OnClick()</c> override, which a directly-raised routed event skips
        /// entirely (plan §28's review, the exact gap that let <c>SettingsToggle</c> and the settings panel
        /// disagree for a whole release candidate).</summary>
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
            CreatedAt: 1_700_000_000 - (n * 60), // Fixed epoch (plan §6.2): no wall clock in the rendered labels.
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }
}
