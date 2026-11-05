using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.Composing;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.App.Views;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L17 (plan §68.9): the timeline keys, end to end through the real <see cref="MainWindow"/>. Every key is a
/// <c>KeyPress</c> on the headless window with keyboard focus where a person would have it (inside the list,
/// put there by the same Esc-from-search path <c>SearchFlipRenderTests</c> already measures, or by a real click
/// on a row), and every assertion is on the side the guarantee changes: the list's own <c>SelectedIndex</c>
/// <em>and</em> the view model's <c>FocusedIndex</c> together (Lead ruling 1: one state), the realized
/// container's position inside the list (the row the key moved to is on screen), the compose box's banner,
/// the column's status line, the clipboard.
///
/// <para>No assertion here is an absolute DIP (plan §68.9's acceptance): L16 changes the row density in
/// parallel, so this class only asks whether a row lies <em>inside</em> the list's bounds, never how tall it is.</para>
///
/// <para>Negative controls run on a <c>git archive</c> scratch while writing this class (exact outputs in the
/// PR body): (1) dropping <c>e.Handled = true</c> for <c>↑</c>/<c>↓</c> in <c>TimelineView.OnListKeyDown</c>
/// turns <see cref="ArrowAndVimKeys_MoveTheCursorOneRow_AndTheListSelectionIsTheSameState"/> red (one <c>↓</c>
/// moves two rows -- Lead ruling 2). (2) changing the <c>SelectedIndex</c> binding to <c>OneWay</c> turns
/// <see cref="ClickingARow_MovesTheSameCursor_AndJContinuesFromIt"/> red (the view model still says -1 after
/// the click). (3) dropping <c>ScrollIntoView</c> from <c>MoveCursor</c> turns
/// <see cref="ShiftJ_GoesToTheLastRow_AndBringsItOnScreen"/> red (no container for the last row).</para>
/// </summary>
public sealed class TimelineKeyboardRenderTests
{
    private const double Width = 900;
    private const double Height = 600;
    private const int NoteCount = 40;
    private const double Epsilon = 0.5;
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    [AvaloniaFact]
    public void ArrowAndVimKeys_MoveTheCursorOneRow_AndTheListSelectionIsTheSameState()
    {
        using var session = new Session();
        session.FocusTimeline();
        session.AssertCursor(-1);

        session.Press(Key.J, PhysicalKey.J);
        session.AssertCursor(0); // fork FocusDown: no cursor -> first row

        session.Press(Key.Down, PhysicalKey.ArrowDown);
        session.AssertCursor(1); // exactly one row: the handler took the key, the ListBox did not also move

        session.Press(Key.Down, PhysicalKey.ArrowDown);
        session.Press(Key.J, PhysicalKey.J);
        session.AssertCursor(3);

        session.Press(Key.K, PhysicalKey.K);
        session.Press(Key.Up, PhysicalKey.ArrowUp);
        session.AssertCursor(1);

        session.Press(Key.Up, PhysicalKey.ArrowUp);
        session.Press(Key.Up, PhysicalKey.ArrowUp);
        session.AssertCursor(-1); // fork FocusUp from the first row clears the cursor
        Assert.True(session.List.IsKeyboardFocusWithin, "clearing the cursor must not throw keyboard focus out of the list");

        session.Press(Key.J, PhysicalKey.J);
        session.AssertCursor(0);
    }

    [AvaloniaFact]
    public void ClickingARow_MovesTheSameCursor_AndJContinuesFromIt()
    {
        using var session = new Session();
        session.FocusTimeline();

        session.ClickRow(2);
        session.AssertCursor(2);

        session.Press(Key.J, PhysicalKey.J);
        session.AssertCursor(3);
    }

    [AvaloniaFact]
    public void ShiftJ_GoesToTheLastRow_AndBringsItOnScreen()
    {
        using var session = new Session();
        session.FocusTimeline();
        Assert.Null(session.List.ContainerFromIndex(NoteCount - 1)); // precondition: 40 rows do not fit in a 600-high window; the last one is not realized

        session.Press(Key.J, PhysicalKey.J, RawInputModifiers.Shift);

        session.AssertCursor(NoteCount - 1);
        session.AssertRowIsOnScreen(NoteCount - 1);

        session.Press(Key.D0, PhysicalKey.Digit0);
        session.AssertCursor(0);
        session.AssertRowIsOnScreen(0);

        session.Press(Key.J, PhysicalKey.J, RawInputModifiers.Shift);
        session.Press(Key.K, PhysicalKey.K, RawInputModifiers.Shift);
        session.AssertCursor(0);
        session.AssertRowIsOnScreen(0);
    }

    [AvaloniaFact]
    public void Escape_ClearsTheCursor_AndTheSelectionHighlightWithIt()
    {
        using var session = new Session();
        session.FocusTimeline();
        session.Press(Key.J, PhysicalKey.J);
        session.Press(Key.J, PhysicalKey.J);
        session.AssertCursor(1);
        Assert.True(((ListBoxItem)session.List.ContainerFromIndex(1)!).IsSelected, "precondition: the focused row is painted selected");

        session.Press(Key.Escape, PhysicalKey.Escape);

        session.AssertCursor(-1);
        Assert.Null(session.List.SelectedItem);
        Assert.False(((ListBoxItem)session.List.ContainerFromIndex(1)!).IsSelected, "the highlight must leave with the cursor");
    }

    [AvaloniaFact]
    public void R_HandsTheFocusedNoteToTheComposeBox()
    {
        using var session = new Session();
        session.FocusTimeline();
        session.Press(Key.J, PhysicalKey.J);
        session.Press(Key.J, PhysicalKey.J);
        var focused = session.Tab.FocusedNote!;

        session.Press(Key.R, PhysicalKey.R);

        Assert.Equal(ComposeMode.Reply, session.Compose.Mode);
        Assert.Equal(focused.Id, session.Compose.Target?.Id);
        var banner = session.Find<TextBlock>("TargetBanner")!;
        Assert.True(banner.IsVisible);
        Assert.StartsWith("Replying to", banner.Text ?? string.Empty, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void CtrlR_RepostsTheFocusedNote_AndTheCursorStaysOnItWhenTheRepostLandsAbove()
    {
        using var session = new Session();
        session.FocusTimeline();
        session.Press(Key.J, PhysicalKey.J);
        session.Press(Key.J, PhysicalKey.J);
        var focused = session.Tab.FocusedNote!;

        session.Press(Key.R, PhysicalKey.R, RawInputModifiers.Control);
        session.Pump(() => session.Tab.Status.StartsWith("Reposted", StringComparison.Ordinal), "the column's status never reported the repost");

        Assert.Contains(session.Tab.Notes, n => n.DisplayContent.StartsWith("(repost)", StringComparison.Ordinal));
        Assert.Same(focused, session.Tab.FocusedNote); // the repost was inserted above (newer), the cursor followed its note...
        session.AssertCursor(session.Tab.Notes.IndexOf(focused)); // ...and the real ListBox agrees on where that is

        session.Press(Key.F, PhysicalKey.F);
        session.Pump(() => session.Tab.Status.StartsWith("Reacted", StringComparison.Ordinal), "the column's status never reported the reaction");
    }

    /// <summary>Plan §68.9: a key pressed without a publisher (no host wiring, no unlocked key) must answer on the
    /// status line, not do nothing. The no-cursor case is the other way a key can have nothing to act on.</summary>
    [AvaloniaFact]
    public void WithoutAPublisher_CtrlR_AnswersOnTheStatusLine_AndSoDoesAKeyWithNoCursor()
    {
        using var session = new Session(withPublisher: false);
        session.FocusTimeline();

        session.Press(Key.R, PhysicalKey.R, RawInputModifiers.Control);
        Assert.Equal(TimelineViewModel.NothingFocusedStatus, session.Tab.Status);

        session.Press(Key.J, PhysicalKey.J);
        session.Press(Key.R, PhysicalKey.R, RawInputModifiers.Control);
        session.Pump(() => session.Tab.Status != TimelineViewModel.NothingFocusedStatus, "the column's status never changed after c+r");

        Assert.Equal("No publisher available yet.", session.Tab.Status);
        Assert.Equal(ComposeMode.Post, session.Compose.Mode); // c+r is not r: the exact-modifier rule
    }

    /// <summary>Fork <c>KeyAssignTrigger.cs:55-95</c>: with the caret in a text box, unmodified keys are typing, not
    /// commands. Measured in both boxes a person types into on this window: <c>r</c> and <c>j</c> land as text,
    /// the cursor does not move, the compose box does not switch to Reply.</summary>
    [AvaloniaFact]
    public void KeysTypedIntoTheSearchBoxOrTheComposeBox_StayThere()
    {
        using var session = new Session();
        session.FocusTimeline();
        session.Press(Key.J, PhysicalKey.J);
        session.AssertCursor(0);

        var searchBox = session.Find<TextBox>("SearchBox")!;
        Assert.True(searchBox.Focus());
        session.Pump();
        session.Press(Key.J, PhysicalKey.J, text: "j");
        session.Press(Key.R, PhysicalKey.R, text: "r");
        Assert.Equal("jr", searchBox.Text);
        session.AssertCursor(0);
        Assert.Equal(ComposeMode.Post, session.Compose.Mode);
        session.Press(Key.Escape, PhysicalKey.Escape); // closes the flip, focus returns to the timeline

        var composeText = session.Find<TextBox>("ComposeText")!;
        Assert.True(composeText.Focus());
        session.Pump();
        session.Press(Key.R, PhysicalKey.R, text: "r");
        session.Press(Key.J, PhysicalKey.J, text: "j");
        Assert.Equal("rj", composeText.Text);
        session.AssertCursor(0);
        Assert.Equal(ComposeMode.Post, session.Compose.Mode);
    }

    [AvaloniaFact]
    public async Task CtrlC_CopiesTheFocusedNotesText_AndSaysSo()
    {
        using var session = new Session();
        session.FocusTimeline();
        session.Press(Key.J, PhysicalKey.J);
        session.Press(Key.J, PhysicalKey.J);
        var focused = session.Tab.FocusedNote!;

        session.Press(Key.C, PhysicalKey.C, RawInputModifiers.Control);
        session.Pump(() => session.Tab.Status.StartsWith("Note text copied", StringComparison.Ordinal), $"the copy never reported; status is '{session.Tab.Status}'");

        var clipboard = TopLevel.GetTopLevel(session.Window)!.Clipboard!;
        Assert.Equal(focused.DisplayContent, await clipboard.TryGetTextAsync());
    }

    /// <summary>A real <see cref="MainWindow"/> at the binding 900x600 (plan §68.1) over <see cref="NoteCount"/>
    /// fixed notes -- more than the list can show, so bring-into-view has work to do -- with a
    /// <see cref="FakeNotePublisher"/> unless a test asks for none. Same shape as <c>SearchFlipRenderTests.Session</c>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public ListBox List { get; }

        public TimelineViewModel Tab { get; }

        public ComposeViewModel Compose { get; }

        public Session(bool withPublisher = true)
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-timeline-keys-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(Enumerable.Range(1, NoteCount).Select(MakeNote).ToList());
            AppServices.NotePublisher = withPublisher ? new FakeNotePublisher() : null;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = Width, Height = Height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Pump();

            Tab = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
            Tab.Query = "from kind(\"1\") where ()";
            Tab.Subscribe();
            Pump(() => !Tab.IsLoading && Find<ListBox>("TimelineList")?.ItemCount == NoteCount, "the timeline never showed the fixture notes");

            List = Find<ListBox>("TimelineList") ?? throw new Xunit.Sdk.XunitException("TimelineList not found");
            Compose = (ComposeViewModel)(Find<ComposeBoxView>("ComposeBox") ?? throw new Xunit.Sdk.XunitException("ComposeBox not found")).DataContext!;
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        /// <summary>Keyboard focus into the list the way the app already puts it there: focus the search box, then
        /// Esc on an empty stack closes the flip and hands focus to the timeline (<c>SearchFocusTarget.Timeline</c>,
        /// measured by <c>SearchFlipRenderTests</c>). No row is selected by this.</summary>
        public void FocusTimeline()
        {
            var searchBox = Find<TextBox>("SearchBox") ?? throw new Xunit.Sdk.XunitException("SearchBox not found");
            Assert.True(searchBox.Focus(), "the search box refused focus");
            Pump();
            Press(Key.Escape, PhysicalKey.Escape);
            Assert.True(List.IsKeyboardFocusWithin, "Esc from the search box must leave keyboard focus inside the timeline list");
            Assert.Equal(-1, List.SelectedIndex);
        }

        public void Press(Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None, string? text = null)
        {
            Window.KeyPress(key, modifiers, physicalKey, null);
            Pump();
            if (text is not null)
            {
                Window.KeyTextInput(text); // a real keyboard follows KeyDown with TextInput; the handler must have left the key alone for this to land
                Pump();
            }

            Window.KeyRelease(key, modifiers, physicalKey, null);
            Pump();
        }

        /// <summary>Real <c>MouseDown</c>/<c>MouseUp</c> at the centre of the row's realized container.</summary>
        public void ClickRow(int index)
        {
            var container = List.ContainerFromIndex(index) ?? throw new Xunit.Sdk.XunitException($"row {index} has no realized container to click");
            var point = container.TranslatePoint(new Point(container.Bounds.Width / 2, container.Bounds.Height / 2), Window)
                ?? throw new Xunit.Sdk.XunitException($"row {index}: not attached to the window's visual tree");
            Window.MouseDown(point, MouseButton.Left);
            Pump();
            Window.MouseUp(point, MouseButton.Left);
            Pump();
        }

        /// <summary>Lead ruling 1 (plan §68.9.1), asserted every time: the list's selection and the view model's
        /// cursor are one number.</summary>
        public void AssertCursor(int expected)
        {
            Assert.Equal(expected, Tab.FocusedIndex);
            Assert.Equal(expected, List.SelectedIndex);
            Assert.Equal(expected < 0 ? null : Tab.Notes[expected], List.SelectedItem);
        }

        /// <summary>The row has a container and that container lies inside the list's own bounds -- not a DIP
        /// value, only containment, so L16's density change cannot turn this red.</summary>
        public void AssertRowIsOnScreen(int index)
        {
            var container = List.ContainerFromIndex(index) ?? throw new Xunit.Sdk.XunitException($"row {index} was not realized after the key moved to it");
            var origin = container.TranslatePoint(new Point(0, 0), List) ?? throw new Xunit.Sdk.XunitException($"row {index}: not attached under the list");
            Assert.True(origin.Y >= -Epsilon && origin.Y + container.Bounds.Height <= List.Bounds.Height + Epsilon,
                $"row {index} should be inside the list's viewport; row y={origin.Y:0.#} h={container.Bounds.Height:0.#}, list h={List.Bounds.Height:0.#}");
            Assert.True(container.IsKeyboardFocusWithin, $"keyboard focus should have moved onto row {index}'s container");
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
            AppServices.NotePublisher = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        /// <summary>Newest first by created_at, so <c>note 01</c> is row 0 and <c>note 40</c> is the last row.</summary>
        private static Note MakeNote(int n) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: new string('b', 64),
            CreatedAt: 1_700_000_000 - n,
            Kind: NoteKind.Text,
            Content: $"note {n:00}",
            Tags: []);
    }
}
