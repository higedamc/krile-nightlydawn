using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Search;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L7a (plan §37): the search flip, end to end through the real <see cref="MainWindow"/>, the real
/// <c>SearchBoxView</c> in the header and the real <c>SearchFlipView</c> over the workspace. Every behaviour
/// here is driven the way a person drives it -- the box is focused through the focus manager
/// (<see cref="Session.FocusSearchBox"/>), text arrives as <c>KeyTextInput</c>, Enter/Esc/↓ as <c>KeyPress</c>,
/// and the backdrop and the mode rows are clicked with a real <c>MouseDown</c>/<c>MouseUp</c> pair -- and every
/// assertion is on the side the guarantee changes (plan §32.4): the rows in the result list, where keyboard focus
/// ended up, whether the panel is on screen, what the box holds after it was closed and reopened. Nothing here
/// asserts that <c>CommitSearch</c> ran.
///
/// <para>Separate class per leaf (plan §3); the two frames land in the shared <c>RenderFrames</c> artifact
/// directory so a reviewer gets the window with the flip closed and open.</para>
///
/// <para>Mutations run by hand while writing this class, each reverted after confirming the colour (exact
/// outputs quoted in the PR body): (1) dropping the as-you-type commit from <c>SearchFlipViewModel.OnTextChanged</c>
/// turns <see cref="TypingInThisTab_FiltersTheResultsAsYouType"/> red (no rows before Enter). (2) dropping the
/// exclusion-word check from <c>KeywordMatcher.IsMatch</c> turns <see cref="ExclusionWord_RemovesANoteThatWouldOtherwiseMatch"/>
/// red ("alpha gamma" stays in the list). (3) turning the positive words from OR into AND turns
/// <see cref="PositiveWords_AreOrEd_NotAndEd"/> red (0 rows, not 3). (4) dropping the close-time cleanup from
/// <c>SearchFlipViewModel.CloseCore</c> turns <see cref="ClosingThePanel_DiscardsTheTextTheResultsAndTheStack"/>
/// red (the previous text is still in the box when it is reopened).</para>
/// </summary>
public sealed class SearchFlipRenderTests
{
    private const double Width = 1280;
    private const double Height = 800;
    private const double Epsilon = 0.5;
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Plan §37.2 item 1: focus alone opens the panel, before anything is typed. The panel sits over
    /// the workspace (same slot, not a row of its own: the columns keep their size underneath) and the rendered
    /// pixels of that slot get darker because the backdrop is painted -- a panel with <c>IsVisible</c> true but
    /// no pixels would pass the first assertions and fail the last.</summary>
    [AvaloniaFact]
    public void FocusingTheBox_OpensThePanelOverTheWorkspace()
    {
        using var session = new Session();

        var flipRoot = session.Find<Panel>("SearchFlipRoot") ?? throw new Xunit.Sdk.XunitException("SearchFlipRoot not found");
        var workspace = session.Find<Control>("Workspace") ?? throw new Xunit.Sdk.XunitException("Workspace not found");
        Assert.False(flipRoot.IsVisible, "the flip must be closed until the search box has focus");

        var workspaceBounds = session.BoundsInWindow(workspace);
        var timelineList = session.Find<ListBox>("TimelineList")!;
        var listWidthBefore = timelineList.Bounds.Width;
        var closed = session.Capture();
        SaveFrame(closed, "closed");
        var closedLuminance = MeanLuminance(closed, workspaceBounds, session.Window.Bounds.Width);

        session.FocusSearchBox();

        Assert.True(flipRoot.IsVisible, "focusing the search box must open the flip");
        var flipBounds = session.BoundsInWindow(flipRoot);
        Assert.True(Math.Abs(flipBounds.X - workspaceBounds.X) < Epsilon && Math.Abs(flipBounds.Y - workspaceBounds.Y) < Epsilon
            && Math.Abs(flipBounds.Width - workspaceBounds.Width) < Epsilon && Math.Abs(flipBounds.Height - workspaceBounds.Height) < Epsilon,
            $"the flip should cover exactly the workspace slot; flip={flipBounds}, workspace={workspaceBounds}");
        Assert.Equal(listWidthBefore, timelineList.Bounds.Width);
        Assert.Equal(Session.SampleContents.Count, timelineList.ItemCount); // the columns keep streaming underneath

        var open = session.Capture();
        SaveFrame(open, "open");
        var openLuminance = MeanLuminance(open, workspaceBounds, session.Window.Bounds.Width);
        Assert.True(closedLuminance - openLuminance > 10,
            $"opening the flip must dim the workspace area; mean luminance closed={closedLuminance:0.#}, open={openLuminance:0.#}");
    }

    /// <summary>Plan §37.2 item 4: in the "This tab" mode every keystroke re-runs the search; the rows change
    /// before Enter is ever pressed, and a keystroke that makes the word match nothing empties the list. The
    /// rows are checked by content, which is what the person reads.</summary>
    [AvaloniaFact]
    public void TypingInThisTab_FiltersTheResultsAsYouType()
    {
        using var session = new Session();
        session.FocusSearchBox();

        session.Type("alp");
        Assert.Equal(["alpha one", "alpha gamma"], session.ResultContents());

        session.Type("x"); // "alpx"
        Assert.Empty(session.ResultContents());
    }

    /// <summary>Plan §37.2 item 10, the <c>-word</c> clause: a note that the positive word accepts is dropped
    /// when it also contains an excluded word.</summary>
    [AvaloniaFact]
    public void ExclusionWord_RemovesANoteThatWouldOtherwiseMatch()
    {
        using var session = new Session();
        session.FocusSearchBox();

        session.Type("alpha -gamma");

        Assert.Equal(["alpha one"], session.ResultContents());
    }

    /// <summary>Plan §37.2 item 10 and §37.3: two positive words are OR-ed (the fork's live rule). No note in
    /// the fixture contains both words, so an AND would show nothing.</summary>
    [AvaloniaFact]
    public void PositiveWords_AreOrEd_NotAndEd()
    {
        using var session = new Session();
        session.FocusSearchBox();

        session.Type("alpha delta");

        Assert.Equal(["alpha one", "alpha gamma", "delta four"], session.ResultContents());
    }

    /// <summary>Plan §37.2 item 3: a <c>?</c> prefix is a KQL query. A bare <c>?</c> says the query is empty, a
    /// half-typed one shows the compiler's error in the error colour, and neither puts rows on screen.</summary>
    [AvaloniaTheory]
    [InlineData("?", SearchFlipViewModel.QueryEmptyText)]
    [InlineData("?from kind(", "Query is invalid")]
    public void QueryMode_ReportsAnEmptyOrInvalidQuery_AndShowsNoRows(string typed, string expectedStatusStart)
    {
        using var session = new Session();
        session.FocusSearchBox();

        session.Type(typed);

        var status = session.Find<TextBlock>("SearchQueryStatus") ?? throw new Xunit.Sdk.XunitException("SearchQueryStatus not found");
        Assert.StartsWith(expectedStatusStart, status.Text ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("error", status.Classes);
        Assert.False(session.Find<Border>("SearchResultHost")!.IsVisible, "an empty or invalid query has no result to show");
        Assert.False(session.Find<ItemsControl>("SearchModeList")!.IsVisible, "the mode list is hidden in query mode (fork :276-300)");
    }

    /// <summary>Plan §37.2 items 3 and 6: a valid <c>?</c> query does not search as you type -- it waits for
    /// Enter -- and Enter then runs it through the same KQL compiler the tabs use, over this tab's notes, and
    /// moves keyboard focus into the result list.</summary>
    [AvaloniaFact]
    public void QueryMode_RunsTheKqlOnEnter_AndMovesFocusToTheResults()
    {
        using var session = new Session();
        session.FocusSearchBox();

        session.Type("?from kind(\"1\") where text contains \"beta\"");

        var status = session.Find<TextBlock>("SearchQueryStatus")!;
        Assert.Equal(SearchFlipViewModel.QueryValidText, status.Text);
        Assert.DoesNotContain("error", status.Classes);
        Assert.False(session.Find<Border>("SearchResultHost")!.IsVisible, "a ? query must not run until Enter (fork :316-322)");

        session.Press(Key.Enter, PhysicalKey.Enter);

        Assert.Equal(["beta two"], session.ResultContents());
        var resultList = session.Find<ListBox>("SearchResultList")!;
        Assert.True(resultList.IsKeyboardFocusWithin, "Enter must move keyboard focus into the results");
        Assert.False(session.SearchBox.IsFocused, "the search box must have given focus up");
    }

    /// <summary>Plan §37.2 item 6 for the keyword mode: the rows are already there from typing, so Enter only
    /// moves focus. Measured through the focus manager, not through a flag.</summary>
    [AvaloniaFact]
    public void Enter_MovesKeyboardFocusIntoTheResultList()
    {
        using var session = new Session();
        session.FocusSearchBox();
        session.Type("alpha");
        Assert.True(session.SearchBox.IsFocused);

        session.Press(Key.Enter, PhysicalKey.Enter);

        var resultList = session.Find<ListBox>("SearchResultList")!;
        Assert.True(resultList.IsKeyboardFocusWithin, "Enter must move keyboard focus into the results");
        Assert.Equal(["alpha one", "alpha gamma"], session.ResultContents());
    }

    /// <summary>Plan §37.2 items 7 and 8: as-you-type commits collapse onto one stack entry, so one Esc empties
    /// the stack, which closes the panel and returns keyboard focus to the timeline the search was over.</summary>
    [AvaloniaFact]
    public void Escape_OnTheLastEntry_ClosesThePanel_AndReturnsFocusToTheTimeline()
    {
        using var session = new Session();
        session.FocusSearchBox();
        session.Type("alpha");
        Assert.NotEmpty(session.ResultContents());

        session.Press(Key.Escape, PhysicalKey.Escape);

        Assert.False(session.Find<Panel>("SearchFlipRoot")!.IsVisible, "Esc on the only stack entry must close the flip");
        var timelineList = session.Find<ListBox>("TimelineList")!;
        Assert.True(timelineList.IsKeyboardFocusWithin, "closing must hand keyboard focus back to the timeline (fork :344-360)");
        Assert.False(session.SearchBox.IsFocused);
    }

    /// <summary>Plan §37.2 item 8 (negative control 4): closing throws the text, the results and the stack away.
    /// Reopening by focus must show an empty box and no rows, and the mode must be back on "This tab" -- shown
    /// by the next word producing rows immediately, which only that mode does.</summary>
    [AvaloniaFact]
    public void ClosingThePanel_DiscardsTheTextTheResultsAndTheStack()
    {
        using var session = new Session();
        session.FocusSearchBox();
        session.Type("alpha");
        session.Press(Key.Down, PhysicalKey.ArrowDown); // mode -> "Loaded notes" (a second stack entry, fork :393-398)
        Assert.True(session.Find<TextBlock>("SearchUnavailableText")!.IsVisible);

        session.ClickBackdrop();

        Assert.False(session.Find<Panel>("SearchFlipRoot")!.IsVisible, "a click on the backdrop must close the flip (fork SearchFlip.xaml:263-270)");

        session.FocusSearchBox();
        Assert.Equal(string.Empty, session.SearchBox.Text ?? string.Empty);
        Assert.False(session.Find<Border>("SearchResultHost")!.IsVisible, "no result may survive a close");
        Assert.False(session.Find<ItemsControl>("SearchModeList")!.IsVisible, "no text, no mode list");

        session.Type("delta");
        Assert.Equal(["delta four"], session.ResultContents()); // "This tab" again: rows without Enter
        Assert.Equal("This tab", session.SelectedModeLabel());

        session.Press(Key.Escape, PhysicalKey.Escape);
        Assert.False(session.Find<Panel>("SearchFlipRoot")!.IsVisible, "the old stack must be gone: one Esc closes, not two");
    }

    /// <summary>Plan §37's rule for the four L7c modes: they can be chosen (real click on the row, ↓ from the
    /// keyboard), the chosen row is painted selected, and choosing one answers with "not implemented" where the
    /// rows were -- never a silent empty panel. The ↓ order is the fork's enum order.</summary>
    [AvaloniaFact]
    public void ChoosingAnUnimplementedMode_SaysSo_InsteadOfDoingNothing()
    {
        using var session = new Session();
        session.FocusSearchBox();
        session.Type("alpha");
        Assert.Equal(2, session.ResultContents().Count);
        Assert.Equal("This tab", session.SelectedModeLabel());

        session.Click(session.ModeButton("Relays"));

        Assert.Equal("Relays", session.SelectedModeLabel());
        var unavailable = session.Find<TextBlock>("SearchUnavailableText")!;
        Assert.True(unavailable.IsVisible);
        Assert.Contains("not implemented", unavailable.Text, StringComparison.Ordinal);
        Assert.False(session.Find<ListBox>("SearchResultList")!.IsVisible, "no rows may be shown for a mode that cannot search yet");
        Assert.True(session.SearchBox.IsFocused, "choosing a mode must leave the caret in the box (the rows are Focusable=False, as in the fork)");

        session.Press(Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("People", session.SelectedModeLabel());

        session.Press(Key.Down, PhysicalKey.ArrowDown); // "Open user" is skipped: "alpha" is not a Nostr identifier
        Assert.Equal("This tab", session.SelectedModeLabel());
        Assert.Equal(2, session.ResultContents().Count); // and "This tab" searches again at once (fork :249)
    }

    /// <summary>Plan §37.2 item 2 / §37.4: the "Open user" row appears only while the text looks like a Nostr
    /// identifier (npub / nprofile / 64-hex / NIP-05), the replacement for the fork's Twitter screen-name test.</summary>
    [AvaloniaTheory]
    [InlineData("alpha", false)]
    [InlineData("npub180cvv07tjdrrgpa0j7j7tmnyl2yr6yr7l8j4s3evf6u64th6gkwsyjh6w6", true)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000001", true)]
    [InlineData("someone@example.com", true)]
    public void OpenUserMode_IsOfferedOnlyForANostrIdentifier(string typed, bool offered)
    {
        using var session = new Session();
        session.FocusSearchBox();

        session.Type(typed);

        Assert.Equal(offered, session.ModeButton("Open user").IsVisible);
    }

    /// <summary>Plan §37.2 item 11 / §41, L7b: clicking the Pin button adds a new tab, selects it, and closes the
    /// flip -- end to end through the real button <c>Click</c>, not <c>_search.PinToTab()</c> (the claim under
    /// test is that the button a person clicks is wired; <c>SearchFlipViewModelPinTests</c> covers the KQL this
    /// produces without Avalonia). Not the fork's <c>RewindStack</c>: plan §41's brief makes pin a destination.</summary>
    [AvaloniaFact]
    public void ClickingPin_AddsANewTab_SelectsIt_AndClosesThePanel()
    {
        using var session = new Session();
        session.FocusSearchBox();
        session.Type("alpha -gamma");
        Assert.Equal(["alpha one"], session.ResultContents());
        Assert.True(session.PinButton.IsVisible);
        Assert.Single(session.Tabs());

        session.Click(session.PinButton);

        Assert.Equal(2, session.Tabs().Count);
        var pinned = session.Tabs()[1];
        Assert.Same(pinned, session.SelectedTab());
        Assert.False(session.Find<Panel>("SearchFlipRoot")!.IsVisible, "pinning must close the flip");
        var list = session.Find<ListBox>("TimelineList");
        Assert.NotNull(list);
        Assert.Same(pinned, list!.DataContext); // The strip actually switched to the pinned tab's own view.
    }

    /// <summary>Plan §37's "a mode that can be chosen must answer, not do nothing", applied to the Pin button
    /// (plan §41 item ⑤): a full column reports through <c>PinErrorText</c> instead of the click doing nothing,
    /// and the flip stays open so the person can see why.</summary>
    [AvaloniaFact]
    public void ClickingPin_WhenTheColumnIsFull_ShowsThePinError_AndLeavesThePanelOpen()
    {
        using var session = new Session();
        var column = ((WorkspaceViewModel)session.Window.DataContext!).Columns[0];
        while (column.CanAddTab)
        {
            column.AddTab();
        }

        session.FocusSearchBox();
        session.Type("alpha");
        Assert.True(session.PinButton.IsVisible);
        var tabCountBefore = session.Tabs().Count;

        session.Click(session.PinButton);

        Assert.Equal(tabCountBefore, session.Tabs().Count);
        Assert.True(session.Find<Panel>("SearchFlipRoot")!.IsVisible, "a failed pin must not close the panel");
        Assert.True(session.PinErrorTextBlock.IsVisible);
        Assert.Contains("already holds", session.PinErrorTextBlock.Text);
    }

    /// <summary>Plan §37.2 item 9: below the fork's 572 DIP result width the mode descriptions are hidden and
    /// the labels stay. Measured by narrowing the real window, not by setting a flag.</summary>
    [AvaloniaFact]
    public void SlimLayout_HidesTheModeDescriptions_BelowTheForkThreshold()
    {
        using var session = new Session();
        session.FocusSearchBox();
        session.Type("alpha");

        var resultArea = session.Find<Panel>("SearchResultArea")!;
        Assert.True(resultArea.Bounds.Width >= SearchFlipViewModel.SlimWidthThreshold, $"at {Width} the result area ({resultArea.Bounds.Width:0.#}) should be wide");
        Assert.True(session.ModeDescription("This tab").IsVisible);

        session.Window.Width = 820;
        session.Pump(() => resultArea.Bounds.Width < SearchFlipViewModel.SlimWidthThreshold, $"the result area never narrowed below {SearchFlipViewModel.SlimWidthThreshold} (now {resultArea.Bounds.Width:0.#})");

        Assert.False(session.ModeDescription("This tab").IsVisible, "slim layout must hide the descriptions");
        Assert.True(session.ModeButton("This tab").IsVisible, "slim layout keeps the mode rows");
    }

    private static void SaveFrame(WriteableBitmap frame, string state)
    {
        Directory.CreateDirectory(MainWindowRenderTests.OutputDirectory);
        frame.Save(
            Path.Combine(MainWindowRenderTests.OutputDirectory, $"searchflip_{state}_{Width:0}x{Height:0}.png"),
            new PngBitmapEncoderOptions());
    }

    /// <summary>Mean luminance of the pixels inside <paramref name="region"/> (window DIPs at scale 1.0), sampled
    /// on a stride so the whole frame is not decoded. Claims "darker", never "these bytes" (plan §6.3).</summary>
    private static double MeanLuminance(WriteableBitmap frame, Rect region, double windowWidth)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var scale = buffer.Size.Width / windowWidth;

        var x0 = (int)(region.X * scale);
        var x1 = Math.Min(buffer.Size.Width, (int)(region.Right * scale));
        var y0 = (int)(region.Y * scale);
        var y1 = Math.Min(buffer.Size.Height, (int)(region.Bottom * scale));

        double sum = 0;
        var samples = 0;
        for (var y = y0; y < y1; y += 3)
        {
            for (var x = x0; x < x1; x += 7)
            {
                var pixel = ReadPixel(bytes, (y * buffer.RowBytes) + (x * 4), buffer.Format);
                sum += (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
                samples++;
            }
        }

        return samples == 0 ? throw new Xunit.Sdk.XunitException($"no pixels sampled in {region}") : sum / samples;
    }

    private static Color ReadPixel(byte[] bytes, int offset, PixelFormat? format)
    {
        var alpha = bytes[offset + 3];

        if (format == PixelFormat.Rgba8888)
        {
            return Color.FromArgb(alpha, bytes[offset], bytes[offset + 1], bytes[offset + 2]);
        }

        if (format == PixelFormat.Bgra8888)
        {
            return Color.FromArgb(alpha, bytes[offset + 2], bytes[offset + 1], bytes[offset]);
        }

        throw new Xunit.Sdk.XunitException($"unexpected framebuffer format {format?.ToString() ?? "(null)"}; this test reads channels by hand and has no mapping for it.");
    }

    /// <summary>A real <see cref="MainWindow"/> over a <see cref="StaticTimelineSourceFactory"/> with four fixed
    /// notes, its one tab subscribed so there is something to search. Same shape as
    /// <see cref="WindowChromeRenderTests"/>'s Session; input goes through the headless window's keyboard and
    /// pointer, never through the view model.</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public static readonly IReadOnlyList<string> SampleContents = ["alpha one", "beta two", "alpha gamma", "delta four"];

        public MainWindow Window { get; }

        public TextBox SearchBox { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-searchflip-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleContents.Select((c, i) => MakeNote(i + 1, c)).ToList());
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = Width, Height = Height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Pump();

            // Give the single default tab a query and subscribe it, as a person would through the settings popup
            // (WindowChromeRenderTests does the same); the fake factory ignores the query text and streams the
            // four notes. The flip itself is never touched here. "where ()" (plan §4's only way to spell an empty
            // predicate) rather than a bare "from kind(...)": L7a never parsed this text, so either spelling was
            // fine then, but L7b's PinToTab does parse it (to AND the keyword expression onto it), and a bare
            // "from" with no "where" is a FilterParseException (KqlParser.ParseWhereClause).
            var tab = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
            tab.Query = "from kind(\"1\") where ()";
            tab.Subscribe();
            Pump(() => !tab.IsLoading && Find<ListBox>("TimelineList")?.ItemCount == SampleContents.Count, "the timeline never showed the fixture notes");

            SearchBox = Find<TextBox>("SearchBox") ?? throw new Xunit.Sdk.XunitException("SearchBox not found");
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public IReadOnlyList<T> FindAll<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

        public Rect BoundsInWindow(Control control)
        {
            var origin = control.TranslatePoint(new Point(0, 0), Window)
                ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached to the window's visual tree");
            return new Rect(origin, control.Bounds.Size);
        }

        /// <summary>A real focus move through the focus manager (fork: the box's <c>GotFocus</c> opens the flip).
        /// Not <c>_search.Open()</c>: the claim under test is that focusing the control a person clicks is wired.</summary>
        public void FocusSearchBox()
        {
            Assert.True(SearchBox.Focus(), "the search box refused focus");
            Pump();
        }

        public void Type(string text)
        {
            Window.KeyTextInput(text);
            Pump();
        }

        public void Press(Key key, PhysicalKey physicalKey)
        {
            Window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
            Pump();
            Window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
            Pump();
        }

        /// <summary>Real <c>MouseDown</c>/<c>MouseUp</c> at the control's own centre (plan §28's review: a
        /// directly raised <c>Click</c> skips the pointer-driven code path).</summary>
        public void Click(Control control) =>
            ClickAt(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
                ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached to the window's visual tree"));

        /// <summary>Clicks the dimmed backdrop where nothing else is drawn: in the navigation column, below the
        /// navigation panel (which is top-aligned), so the hit lands on <c>SearchBackdrop</c> itself.</summary>
        public void ClickBackdrop()
        {
            var navigation = Find<Border>("SearchNavigation") ?? throw new Xunit.Sdk.XunitException("SearchNavigation not found");
            var navigationBounds = BoundsInWindow(navigation);
            var flipBounds = BoundsInWindow(Find<Panel>("SearchFlipRoot")!);
            var point = new Point(navigationBounds.X + (navigationBounds.Width / 2), navigationBounds.Bottom + 40);
            Assert.True(point.Y < flipBounds.Bottom, $"no exposed backdrop below the navigation panel ({navigationBounds.Bottom:0.#} of {flipBounds.Bottom:0.#})");
            ClickAt(point);
        }

        public IReadOnlyList<string> ResultContents()
        {
            var list = Find<ListBox>("SearchResultList") ?? throw new Xunit.Sdk.XunitException("SearchResultList not found");
            return list.Items.OfType<NoteRow>().Select(r => r.DisplayContent).ToList();
        }

        public Button ModeButton(string label) =>
            FindAll<Button>("SearchModeButton").FirstOrDefault(b => b.Tag is SearchModeOption o && o.Label == label)
            ?? throw new Xunit.Sdk.XunitException($"no mode row labelled '{label}'");

        public TextBlock ModeDescription(string label) =>
            ModeButton(label).GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "SearchModeDescription");

        public string SelectedModeLabel() =>
            Assert.Single(FindAll<Button>("SearchModeButton"), b => b.Classes.Contains("selected")).Tag is SearchModeOption o
                ? o.Label
                : throw new Xunit.Sdk.XunitException("the selected mode row has no option");

        public Button PinButton => Find<Button>("PinToTabButton") ?? throw new Xunit.Sdk.XunitException("PinToTabButton not found");

        public TextBlock PinErrorTextBlock => Find<TextBlock>("PinErrorText") ?? throw new Xunit.Sdk.XunitException("PinErrorText not found");

        public IReadOnlyList<TimelineViewModel> Tabs() => ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs;

        public TimelineViewModel? SelectedTab() => ((WorkspaceViewModel)Window.DataContext!).Columns[0].SelectedTab;

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

        public WriteableBitmap Capture() =>
            Window.CaptureRenderedFrame() ?? throw new Xunit.Sdk.XunitException("CaptureRenderedFrame returned null");

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private void ClickAt(Point point)
        {
            Window.MouseDown(point, MouseButton.Left);
            Pump();
            Window.MouseUp(point, MouseButton.Left);
            Pump();
        }

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - (n * 60), // Fixed epoch (plan §6.2): no wall clock in the rendered labels.
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }
}
