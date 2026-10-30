using System.Diagnostics;
using NightlyDawn.App.Composing;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>
/// L17 (plan §68.9): the keyboard cursor without Avalonia. <c>postToUi</c> runs inline, so
/// <see cref="TimelineViewModel.FocusedIndex"/> and <see cref="TimelineViewModel.Status"/> are read directly.
/// The move semantics are the fork's (<c>TimelineViewModelBase.cs:361-388</c>): down from no cursor lands on
/// the first row, up from the first row clears the cursor, the last row is a wall. The two facts a bound
/// <c>ListBox</c> would otherwise have to be trusted for -- the cursor following its note under a newer insert,
/// and dying with its row at the cap -- are asserted here on the view model alone, and again through the real
/// list in <c>TimelineKeyboardRenderTests</c>.
///
/// <para>Negative controls run on a <c>git archive</c> scratch while writing this class (exact outputs in the
/// PR body): deleting the <c>focusedBefore >= index</c> shift in <c>AddNote</c> turns
/// <see cref="ANewerNoteInsertedAboveTheCursor_LeavesTheCursorOnTheSameNote"/> red (the cursor stays at 1 and
/// now names the new note); deleting the trailing cap check turns
/// <see cref="CappingAwayTheFocusedRow_ClearsTheCursor"/> red (<c>FocusedIndex</c> is 3 in a 3-row list).</para>
/// </summary>
public class TimelineViewModelFocusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task FocusDown_FromNoCursor_GoesToTheFirstRow_AndTheLastRowIsAWall()
    {
        using var vm = await LoadAsync(3);
        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);
        Assert.Null(vm.FocusedNote);

        vm.FocusDown();
        Assert.Equal(0, vm.FocusedIndex);
        Assert.Same(vm.Notes[0], vm.FocusedNote);

        vm.FocusDown();
        vm.FocusDown();
        Assert.Equal(2, vm.FocusedIndex);

        vm.FocusDown(); // fork :374: past the end does nothing
        Assert.Equal(2, vm.FocusedIndex);
    }

    [Fact]
    public async Task FocusUp_FromTheFirstRow_ClearsTheCursor_AndDoesNothingWithoutOne()
    {
        using var vm = await LoadAsync(3);

        vm.FocusUp(); // no cursor: nothing to move (fork :363)
        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);

        vm.FocusDown();
        vm.FocusDown();
        vm.FocusUp();
        Assert.Equal(0, vm.FocusedIndex);

        vm.FocusUp(); // fork :365: index -1 => FocusedStatus = null
        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);
        Assert.Null(vm.FocusedNote);
    }

    [Fact]
    public async Task FocusTop_FocusBottom_AndClearFocus()
    {
        using var vm = await LoadAsync(5);

        vm.FocusBottom();
        Assert.Equal(4, vm.FocusedIndex);

        vm.FocusTop();
        Assert.Equal(0, vm.FocusedIndex);

        vm.FocusBottom();
        vm.ClearFocus();
        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);
    }

    [Fact]
    public void AnEmptyColumn_NeverGetsACursor()
    {
        using var vm = NewViewModel(new FakeTimelineSourceFactory());

        vm.FocusDown();
        vm.FocusUp();
        vm.FocusTop();
        vm.FocusBottom();
        vm.FocusedIndex = 0;

        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);
        Assert.Null(vm.FocusedNote);
    }

    /// <summary>The setter is the two-way binding's write path: whatever a <c>ListBox</c> pushes that does not
    /// name a row (its own -1, or a stale index during a reset) must land as <see cref="TimelineViewModel.NoFocus"/>,
    /// never as an index <see cref="TimelineViewModel.FocusedNote"/> would throw on.</summary>
    [Theory]
    [InlineData(-1, -1)]
    [InlineData(-7, -1)]
    [InlineData(2, 2)]
    [InlineData(3, -1)]
    [InlineData(99, -1)]
    public async Task Setter_AcceptsRowIndexes_AndClampsEverythingElseToNoFocus(int pushed, int expected)
    {
        using var vm = await LoadAsync(3);

        vm.FocusedIndex = pushed;

        Assert.Equal(expected, vm.FocusedIndex);
    }

    [Fact]
    public async Task ANewerNoteInsertedAboveTheCursor_LeavesTheCursorOnTheSameNote()
    {
        using var vm = await LoadAsync(3); // created_at 300, 200, 100 -> indexes 0, 1, 2
        vm.FocusedIndex = 1;
        var focused = vm.FocusedNote!;
        var moved = new List<string?>();
        vm.PropertyChanged += (_, e) => moved.Add(e.PropertyName);

        vm.NotePublished(TestNotes.Make(10, createdAt: 400), new PublishResult([])); // newest: lands at index 0

        Assert.Equal(2, vm.FocusedIndex);
        Assert.Same(focused, vm.FocusedNote); // the note, not the slot (fork: FocusedStatus is a reference)
        Assert.Contains(nameof(TimelineViewModel.FocusedIndex), moved); // the bound list is told
    }

    [Fact]
    public async Task ANoteInsertedBelowTheCursor_DoesNotMoveIt()
    {
        using var vm = await LoadAsync(3);
        vm.FocusedIndex = 1;
        var focused = vm.FocusedNote!;

        vm.NotePublished(TestNotes.Make(10, createdAt: 50), new PublishResult([])); // oldest: lands at index 3

        Assert.Equal(1, vm.FocusedIndex);
        Assert.Same(focused, vm.FocusedNote);
    }

    [Fact]
    public async Task CappingAwayTheFocusedRow_ClearsTheCursor()
    {
        using var vm = await LoadAsync(3, maxNotes: 3);
        vm.FocusBottom();
        Assert.Equal(2, vm.FocusedIndex);

        vm.NotePublished(TestNotes.Make(10, createdAt: 400), new PublishResult([])); // pushes the focused row past the cap

        Assert.Equal(3, vm.Notes.Count);
        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);
        Assert.Null(vm.FocusedNote);
    }

    [Fact]
    public async Task CappingAwayAnotherRow_KeepsTheCursorOnItsNote()
    {
        using var vm = await LoadAsync(3, maxNotes: 3);
        vm.FocusedIndex = 1;
        var focused = vm.FocusedNote!;

        vm.NotePublished(TestNotes.Make(10, createdAt: 400), new PublishResult([]));

        Assert.Equal(2, vm.FocusedIndex);
        Assert.Same(focused, vm.FocusedNote);
    }

    [Fact]
    public async Task Subscribe_ClearsTheCursorWithTheColumn()
    {
        var factory = new FakeTimelineSourceFactory();
        using var vm = await LoadAsync(3, factory: factory);
        vm.FocusBottom();

        factory.Enqueue(new FakeTimelineSource());
        vm.Query = "from mentions";
        vm.Subscribe();

        Assert.Empty(vm.Notes);
        Assert.Equal(TimelineViewModel.NoFocus, vm.FocusedIndex);
    }

    [Fact]
    public async Task ReplyToFocused_WithNoCursor_SaysSoOnStatus()
    {
        using var vm = await LoadAsync(3);

        vm.ReplyToFocused();

        Assert.Equal(TimelineViewModel.NothingFocusedStatus, vm.Status);
    }

    [Fact]
    public async Task ReplyToFocused_OnARowWithoutActions_SaysSoOnStatus()
    {
        using var vm = await LoadAsync(3); // no actionsFactory: every row's Actions is null (designer/sample path)
        vm.FocusDown();

        vm.ReplyToFocused();

        Assert.Equal(TimelineViewModel.NoActionsStatus, vm.Status);
    }

    [Fact]
    public async Task ReplyToFocused_HandsTheFocusedNoteToTheComposeBox()
    {
        var (vm, _, compose) = await LoadWithActionsAsync();
        using var _vm = vm;
        vm.FocusedIndex = 1;
        var focused = vm.FocusedNote!;

        vm.ReplyToFocused();

        Assert.Equal(ComposeMode.Reply, compose.Mode);
        Assert.Equal(focused.Id, compose.Target?.Id);
    }

    [Fact]
    public async Task ReplyAndRepost_OnARepostRow_AreRefusedLikeTheDisabledButtons()
    {
        var (vm, publisher, compose) = await LoadWithActionsAsync(kind: NoteKind.Repost);
        using var _vm = vm;
        vm.FocusDown();
        Assert.False(vm.FocusedNote!.CanReplyOrRepost);

        vm.ReplyToFocused();
        Assert.Equal(TimelineViewModel.NotATextNoteStatus, vm.Status);
        Assert.Equal(ComposeMode.Post, compose.Mode);

        await vm.RepostFocusedAsync();
        Assert.Equal(TimelineViewModel.NotATextNoteStatus, vm.Status);
        Assert.Empty(publisher.Calls);

        await vm.ReactToFocusedAsync(); // a reaction has no kind guard (the ♥ button is always enabled)
        Assert.Equal("React", Assert.Single(publisher.Calls).Method);
    }

    /// <summary>Plan §68.9's "pressing a key without an unlocked key must neither crash nor stay silent": the
    /// publisher throws the exception the real one throws before a key is unlocked, and the result is a line on
    /// Status, through <see cref="NoteRowActions"/>'s own catch (plan §10.4-2) -- nothing escapes to the caller.</summary>
    [Fact]
    public async Task RepostFocusedAsync_WithoutAnUnlockedKey_ReportsOnStatus_AndDoesNotThrow()
    {
        var (vm, publisher, _) = await LoadWithActionsAsync();
        using var _vm = vm;
        publisher.RejectWith = new SignerUnavailableException("No local key is active.");
        vm.FocusDown();

        await vm.RepostFocusedAsync();

        Assert.Equal("Repost failed: SignerUnavailableException.", vm.Status);
    }

    [Fact]
    public async Task RepostAndReact_OnTheFocusedTextRow_GoThroughTheRowsOwnActions()
    {
        var (vm, publisher, _) = await LoadWithActionsAsync();
        using var _vm = vm;
        vm.FocusedIndex = 2;
        var focused = vm.FocusedNote!;

        await vm.RepostFocusedAsync();
        Assert.StartsWith("Reposted · ", vm.Status, StringComparison.Ordinal);
        Assert.Equal(focused.Id, Assert.Single(publisher.Calls).Target?.Id);
        Assert.Same(focused, vm.FocusedNote); // the repost landed in the column above the cursor; the cursor followed its note

        await vm.ReactToFocusedAsync();
        Assert.StartsWith("Reacted · ", vm.Status, StringComparison.Ordinal);
        Assert.Equal(("React", focused.Id), (publisher.Calls[1].Method, publisher.Calls[1].Target?.Id));
    }

    [Fact]
    public async Task FocusedContentForCopy_ReturnsTheFullNoteText_NotTheTruncatedRow()
    {
        var longContent = new string('x', NoteRow.MaxContentChars + 500);
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source));
        vm.Query = "from home";
        vm.Subscribe();
        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100, content: longContent)));
        source.Push(new InitialLoadComplete());
        await WaitUntil(() => vm.Notes.Count == 1 && !vm.IsLoading, "the long note was shown");
        vm.FocusDown();
        Assert.True(vm.FocusedNote!.DisplayContent.Length < longContent.Length, "precondition: the row itself is truncated");

        Assert.Equal(longContent, vm.FocusedContentForCopy());
    }

    [Fact]
    public async Task FocusedContentForCopy_WithNoCursor_ReturnsNull_AndSaysSo()
    {
        using var vm = await LoadAsync(3);

        Assert.Null(vm.FocusedContentForCopy());
        Assert.Equal(TimelineViewModel.NothingFocusedStatus, vm.Status);
    }

    private static TimelineViewModel NewViewModel(ITimelineSourceFactory factory, int maxNotes = TimelineViewModel.DefaultMaxNotes, Func<Note, NoteRowActions?>? actionsFactory = null) =>
        new(factory, postToUi: action => action(), maxNotes, actionsFactory: actionsFactory);

    /// <summary><paramref name="count"/> notes, created_at 100·count down to 100, so index 0 is the newest.</summary>
    private static async Task<TimelineViewModel> LoadAsync(int count, int maxNotes = TimelineViewModel.DefaultMaxNotes, FakeTimelineSourceFactory? factory = null)
    {
        factory ??= new FakeTimelineSourceFactory();
        var vm = NewViewModel(factory, maxNotes);
        await LoadIntoAsync(vm, factory, count, NoteKind.Text);
        return vm;
    }

    private static async Task LoadIntoAsync(TimelineViewModel vm, FakeTimelineSourceFactory factory, int count, NoteKind kind)
    {
        var source = new FakeTimelineSource();
        factory.Enqueue(source);
        vm.Query = "from home";
        vm.Subscribe();
        for (var n = 1; n <= count; n++)
        {
            source.Push(new NoteArrived(TestNotes.Make(n, createdAt: 100L * n, kind: kind)));
        }

        source.Push(new InitialLoadComplete());
        await WaitUntil(() => vm.Notes.Count == count && !vm.IsLoading, $"{count} notes loaded (status: {vm.Status})");
    }

    /// <summary>Rows with real <see cref="NoteRowActions"/> over a <see cref="FakeNotePublisher"/>, reporting to the
    /// column's own Status the way <c>MainWindow.CreateTab</c> wires it: the view model is constructed first and the
    /// closure reads it lazily, at row-build time.</summary>
    private static async Task<(TimelineViewModel Vm, FakeNotePublisher Publisher, ComposeViewModel Compose)> LoadWithActionsAsync(NoteKind kind = NoteKind.Text)
    {
        var publisher = new FakeNotePublisher();
        var factory = new FakeTimelineSourceFactory();
        TimelineViewModel? vm = null;
        var compose = new ComposeViewModel(() => publisher, () => vm, postToUi: action => action());
        vm = NewViewModel(factory, actionsFactory: note => new NoteRowActions(note, () => publisher, compose, vm!, vm!.ReportStatus));
        await LoadIntoAsync(vm, factory, 3, kind);
        return (vm, publisher, compose);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > Timeout)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(10);
        }
    }
}
