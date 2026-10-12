using NightlyDawn.App.Persistence;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Theming;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests.Persistence;

/// <summary>
/// <see cref="LayoutPersistence"/> without Avalonia (same pattern as <see cref="ColumnViewModelTests"/>: inline
/// <c>postToUi</c>). The render tests cover the shipped store and the real window; this class covers the
/// ordering rules plan §32.7 spells out -- nothing saved before or during restore, the built-in default never
/// saved, debounce coalescing, failures reported rather than thrown, and a bounded close flush.
/// </summary>
public sealed class LayoutPersistenceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeAppStateStore _store = new();
    private readonly FakeTimelineSourceFactory _factory = new();
    private readonly List<string> _problems = [];
    private readonly List<AppTheme> _applied = [];
    private readonly WorkspaceViewModel _workspace;
    private readonly ThemeSwitcherViewModel _theme;

    public LayoutPersistenceTests()
    {
        _workspace = new WorkspaceViewModel(() => new TimelineViewModel(_factory, action => action()));
        _theme = new ThemeSwitcherViewModel(AppTheme.System, _applied.Add);
    }

    private LayoutPersistence NewPersistence(TimeSpan? debounce = null) =>
        new(_store, _workspace, _theme, _problems.Add, debounce ?? TimeSpan.FromMilliseconds(40));

    private static AppState SavedLayout() => new(
        AppState.CurrentSchemaVersion,
        [
            new Column("col-a", [new Timeline("tab-1", "from home", "from home"), new Timeline("tab-2", "", "")], SelectedTabId: "tab-2"),
            new Column("col-b", [new Timeline("tab-3", "from kind(\"1\")", "from kind(\"1\")")], SelectedTabId: "tab-3"),
        ],
        ActiveColumnId: "col-b",
        new Settings(Theme: AppTheme.Dark));

    [Fact]
    public void Restore_RebuildsColumnsTabsQueriesAndSelection_AndSubscribesTheNonBlankOnes()
    {
        _store.LoadResult = SavedLayout();
        _factory.Enqueue(new FakeTimelineSource()).Enqueue(new FakeTimelineSource());
        using var persistence = NewPersistence();

        Assert.True(persistence.Restore());

        Assert.Equal(["col-a", "col-b"], _workspace.Columns.Select(c => c.Id));
        Assert.Equal("col-b", _workspace.SelectedColumn!.Id);
        var colA = _workspace.Columns[0];
        Assert.Equal(["tab-1", "tab-2"], colA.Tabs.Select(t => t.Id));
        Assert.Equal(["from home", ""], colA.Tabs.Select(t => t.Query));
        Assert.Equal("tab-2", colA.SelectedTab!.Id);
        Assert.Equal("tab-3", _workspace.Columns[1].SelectedTab!.Id);

        // Two tabs had a query, one was blank: exactly two subscriptions, and they carry the restored queries.
        Assert.Equal(["from home", "from kind(\"1\")"], _factory.AnonymousCalls.Select(t => t.KqlQuery));
        Assert.Null(colA.Tabs[1].ActiveTimeline);
    }

    [Fact]
    public void Restore_AppliesTheSavedTheme()
    {
        _store.LoadResult = SavedLayout();
        _factory.Enqueue(new FakeTimelineSource()).Enqueue(new FakeTimelineSource());
        using var persistence = NewPersistence();

        persistence.Restore();

        Assert.Equal(AppTheme.Dark, _theme.Selected.Value);
        Assert.Equal([AppTheme.Dark], _applied);
    }

    [Fact]
    public async Task Restore_WithTheDefaultState_KeepsTheBuiltInLayout_AndNeverSaves()
    {
        var builtIn = _workspace.Columns.Single();
        using var persistence = NewPersistence();

        Assert.False(persistence.Restore());
        await persistence.FlushAsync();

        Assert.Same(builtIn, _workspace.Columns.Single()); // Not rebuilt, not replaced.
        Assert.Equal(AppTheme.System, _theme.Selected.Value);
        Assert.Empty(_applied);
        Assert.Empty(_store.Saved);
        Assert.Equal(0, persistence.SaveAttempts);
        Assert.Empty(_problems);
    }

    [Fact]
    public async Task Restore_ItselfNeverSaves_EvenThoughItChangesEverything()
    {
        _store.LoadResult = SavedLayout();
        _factory.Enqueue(new FakeTimelineSource()).Enqueue(new FakeTimelineSource());
        using var persistence = NewPersistence(TimeSpan.Zero);

        persistence.Restore();
        await Task.Delay(100);
        await persistence.FlushAsync();

        Assert.Empty(_store.Saved);
        Assert.Equal(0, persistence.SaveAttempts);
    }

    [Fact]
    public async Task Restore_WhenTheStoreThrows_KeepsTheDefault_Reports_AndStillSavesLaterChanges()
    {
        _store.ThrowOnLoad = new IOException("disk on fire");
        using var persistence = NewPersistence();

        Assert.False(persistence.Restore());

        Assert.Contains("could not load", Assert.Single(_problems));
        Assert.Single(_workspace.Columns);

        _workspace.AddColumn();
        await persistence.FlushAsync();
        Assert.Equal(2, Assert.Single(_store.Saved).Columns.Count);
    }

    [Fact]
    public void Restore_Twice_Throws()
    {
        using var persistence = NewPersistence();
        persistence.Restore();

        Assert.Throws<InvalidOperationException>(() => persistence.Restore());
    }

    [Fact]
    public async Task ChangesBeforeRestore_AreNotSaved()
    {
        using var persistence = NewPersistence();

        _workspace.Columns[0].Tabs[0].Query = "typed before restore";
        await persistence.FlushAsync();

        Assert.Empty(_store.Saved);
    }

    [Fact]
    public async Task QueryEdits_AreCoalescedIntoOneSave_CarryingTheLatestText()
    {
        using var persistence = NewPersistence(TimeSpan.FromMilliseconds(60));
        persistence.Restore();
        var tab = _workspace.Columns[0].Tabs[0];

        foreach (var partial in new[] { "f", "fr", "fro", "from", "from home" })
        {
            tab.Query = partial;
            await Task.Delay(5);
        }

        await WaitUntil(() => _store.Saved.Count >= 1);
        await Task.Delay(200); // Long enough for any second write to have happened if one were coming.

        var saved = Assert.Single(_store.Saved);
        Assert.Equal("from home", saved.Columns[0].Tabs[0].KqlQuery);
        Assert.Equal("from home", saved.Columns[0].Tabs[0].Title);
        Assert.Equal(1, persistence.SaveAttempts);
    }

    [Fact]
    public async Task StructuralChanges_AreSaved_WithIdsAndSelection()
    {
        using var persistence = NewPersistence();
        persistence.Restore();

        var second = _workspace.AddColumn();
        var extraTab = second.AddTab();
        extraTab.Query = "from kind(\"1\")";
        second.SelectedTab = second.Tabs[0];
        await persistence.FlushAsync();

        var saved = _store.Saved[^1];
        Assert.Equal([_workspace.Columns[0].Id, second.Id], saved.Columns.Select(c => c.Id));
        Assert.Equal(second.Id, saved.ActiveColumnId);
        Assert.Equal([second.Tabs[0].Id, extraTab.Id], saved.Columns[1].Tabs.Select(t => t.Id));
        Assert.Equal(second.Tabs[0].Id, saved.Columns[1].SelectedTabId);
        Assert.Equal("from kind(\"1\")", saved.Columns[1].Tabs[1].KqlQuery);
        Assert.Equal(AppState.CurrentSchemaVersion, saved.SchemaVersion);
    }

    [Fact]
    public async Task ClosingATab_AndAColumn_AreSaved()
    {
        using var persistence = NewPersistence();
        persistence.Restore();
        var second = _workspace.AddColumn();
        var tab = second.AddTab();
        await persistence.FlushAsync();
        var before = _store.Saved.Count;

        second.CloseTab(tab);
        await persistence.FlushAsync();
        Assert.Single(_store.Saved[^1].Columns[1].Tabs);

        _workspace.CloseColumn(second);
        await persistence.FlushAsync();
        Assert.Single(_store.Saved[^1].Columns);
        Assert.Equal(before + 2, _store.Saved.Count);
    }

    [Fact]
    public async Task ThemeChange_IsSaved_AndCarriesTheTheme()
    {
        using var persistence = NewPersistence();
        persistence.Restore();

        _theme.Selected = ThemeSwitcherViewModel.Options.Single(o => o.Value == AppTheme.Light);
        await persistence.FlushAsync();

        Assert.Equal(AppTheme.Light, Assert.Single(_store.Saved).Settings.Theme);
    }

    [Fact]
    public async Task NoteAndStatusChurn_DoesNotSave()
    {
        var source = new FakeTimelineSource();
        _factory.Enqueue(source);
        using var persistence = NewPersistence();
        persistence.Restore();
        var tab = _workspace.Columns[0].Tabs[0];
        tab.Query = "from kind(\"1\")";
        await persistence.FlushAsync();
        var saves = _store.Saved.Count;

        tab.Subscribe(); // ActiveTimeline, Status, IsLoading all change.
        source.Push(new NoteArrived(TestNotes.Make(1, 1_700_000_000)));
        source.Push(new InitialLoadComplete());
        source.Complete();
        await tab.StreamCompletion.WaitAsync(Timeout);
        tab.ReportStatus("posted");
        await persistence.FlushAsync();

        Assert.Equal(saves, _store.Saved.Count);
    }

    [Fact]
    public async Task SaveFailure_IsReported_NotThrown_AndTheNextChangeTriesAgain()
    {
        _store.ThrowOnSave = new ArgumentException("Refusing to write a state the next launch would discard: test.");
        using var persistence = NewPersistence();
        persistence.Restore();

        _workspace.AddColumn();
        await persistence.FlushAsync();

        Assert.Contains("could not save the layout (ArgumentException", Assert.Single(_problems));
        Assert.Empty(_store.Saved);

        _store.ThrowOnSave = null;
        _workspace.AddColumn();
        await persistence.FlushAsync();
        Assert.Equal(3, Assert.Single(_store.Saved).Columns.Count);
    }

    [Fact]
    public void FlushOnClose_WritesThePendingSnapshot()
    {
        using var persistence = NewPersistence(TimeSpan.FromMinutes(1)); // The timer would never fire inside the test.
        persistence.Restore();
        _workspace.AddColumn();

        Assert.True(persistence.FlushOnClose(Timeout));

        Assert.Equal(2, Assert.Single(_store.Saved).Columns.Count);
        Assert.Empty(_problems);
    }

    [Fact]
    public void FlushOnClose_GivesUpAtTheDeadline_WhenTheStoreNeverAnswers()
    {
        _store.SaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var persistence = NewPersistence(TimeSpan.FromMinutes(1));
        persistence.Restore();
        _workspace.AddColumn();

        var started = DateTime.UtcNow;
        var completed = persistence.FlushOnClose(TimeSpan.FromMilliseconds(150));
        var elapsed = DateTime.UtcNow - started;

        Assert.False(completed);
        Assert.True(elapsed < Timeout, $"FlushOnClose blocked for {elapsed.TotalSeconds:0.##} s; it must give up at the deadline");
        Assert.Contains("did not finish", Assert.Single(_problems));
        _store.SaveGate.TrySetResult(); // Let the stuck save drain so the test host exits cleanly.
    }

    [Fact]
    public async Task Dispose_StopsListening()
    {
        var persistence = NewPersistence();
        persistence.Restore();
        persistence.Dispose();

        _workspace.AddColumn();
        await persistence.FlushAsync();

        Assert.Empty(_store.Saved);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10);
        }
    }
}
