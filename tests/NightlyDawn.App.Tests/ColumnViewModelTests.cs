using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>Exercises <see cref="ColumnViewModel"/> without Avalonia, same pattern as
/// <see cref="TimelineViewModelTests"/>: <c>postToUi</c> runs inline, so state is observable directly.</summary>
public class ColumnViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TimelineViewModel NewTab() =>
        new(new FakeTimelineSourceFactory(), postToUi: action => action());

    [Fact]
    public void AddTab_AppendsAndSelectsIt()
    {
        var column = new ColumnViewModel(NewTab);

        var first = column.AddTab();
        Assert.Same(first, Assert.Single(column.Tabs));
        Assert.Same(first, column.SelectedTab);

        var second = column.AddTab();
        Assert.Equal([first, second], column.Tabs);
        Assert.Same(second, column.SelectedTab); // the newest tab becomes selected, not the first.
    }

    [Fact]
    public async Task CloseTab_DisposesIt_AndStopsItsSubscription()
    {
        // Required negative control (plan §11.3): drop `tab.Dispose()` from CloseTab and this test
        // times out waiting for Cancelled instead of completing -- "Dispose was called" alone (without
        // observing the underlying stream actually stop) would not catch that mutation.
        var source = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(source);
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var tab = column.AddTab();
        tab.Query = "from home";
        tab.Subscribe();

        column.CloseTab(tab);

        await source.Cancelled.WaitAsync(Timeout);
        Assert.Throws<ObjectDisposedException>(tab.Subscribe);
    }

    [Fact]
    public void AddTab_WiresRequestClose_SoTheTabCanAskToBeRemoved()
    {
        // Required negative control (plan §11.6 / PR #30 review, carried into §21.2): RequestClose moved the
        // tab's own close button into TimelineView's header row, closing over `tab` the same way CreateTab's
        // actionsFactory closes over its own tab. If AddTab instead closed over the column's first tab (the
        // same class of mutation #30's review caught for actionsFactory), the second tab's RequestClose
        // would remove the first one instead -- this asserts it removes itself.
        var column = new ColumnViewModel(NewTab);
        var first = column.AddTab();
        var second = column.AddTab();

        second.RequestClose!.Invoke();

        Assert.Equal([first], column.Tabs);
    }

    [Fact]
    public void CloseTab_WhenItWasSelected_SelectsTheNextRemainingTab_OrNullIfNoneAreLeft()
    {
        var column = new ColumnViewModel(NewTab);
        var first = column.AddTab();
        var second = column.AddTab();
        column.SelectedTab = first;

        column.CloseTab(first);
        Assert.Same(second, column.SelectedTab);

        column.CloseTab(second);
        Assert.Empty(column.Tabs); // plan §11.1: a column can reach zero tabs.
        Assert.Null(column.SelectedTab);
    }

    [Fact]
    public void CloseTab_WhenItWasNotSelected_LeavesTheSelectionAlone()
    {
        var column = new ColumnViewModel(NewTab);
        var first = column.AddTab();
        var second = column.AddTab();
        column.SelectedTab = second;

        column.CloseTab(first);

        Assert.Same(second, column.SelectedTab);
    }

    [Fact]
    public void CloseTab_NotInThisColumn_IsANoOp()
    {
        var column = new ColumnViewModel(NewTab);
        var tab = column.AddTab();
        var strayTab = NewTab();

        column.CloseTab(strayTab);

        Assert.Equal([tab], column.Tabs);
        Assert.Same(tab, column.SelectedTab);
    }

    [Fact]
    public async Task Dispose_DisposesEveryTab()
    {
        var sourceA = new FakeTimelineSource();
        var sourceB = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(sourceA).Enqueue(sourceB);
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var tabA = column.AddTab();
        tabA.Query = "from home";
        tabA.Subscribe();
        var tabB = column.AddTab();
        tabB.Query = "from mentions";
        tabB.Subscribe();

        column.Dispose();

        await sourceA.Cancelled.WaitAsync(Timeout);
        await sourceB.Cancelled.WaitAsync(Timeout);
        Assert.Throws<ObjectDisposedException>(tabA.Subscribe);
        Assert.Throws<ObjectDisposedException>(tabB.Subscribe);
    }

    [Fact]
    public void PropertyChanged_FiresForSelectedTab()
    {
        var column = new ColumnViewModel(NewTab);
        var changed = new List<string?>();
        column.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        column.AddTab(); // Tabs changed (so CanAddTab is re-announced, L9-2), then SelectedTab is set inside AddTab.

        Assert.Equal(["CanAddTab", "SelectedTab"], changed);
    }

    [Fact]
    public void AddTab_StopsAtTheContractBound_AndSaysSoThroughCanAddTab()
    {
        var column = new ColumnViewModel(NewTab);
        var canAddChanges = 0;
        column.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ColumnViewModel.CanAddTab))
            {
                canAddChanges++;
            }
        };

        for (var i = 0; i < AppState.MaxTabsPerColumn; i++) // Bounded by the constant: see WorkspaceViewModelTests.
        {
            column.AddTab();
        }

        Assert.False(column.CanAddTab);

        // L9-2 (plan §34): exactly the store's bound, so a column filled through this class is always one the
        // store will save; before this, the 33rd tab made every later save fail for the rest of the session.
        Assert.Equal(AppState.MaxTabsPerColumn, column.Tabs.Count);
        Assert.Equal(AppState.MaxTabsPerColumn, canAddChanges); // One notification per add, including the first.

        var ex = Assert.Throws<InvalidOperationException>(() => column.AddTab());
        Assert.Contains(nameof(AppState.MaxTabsPerColumn), ex.Message, StringComparison.Ordinal);
        Assert.Equal(AppState.MaxTabsPerColumn, column.Tabs.Count);

        column.CloseTab(column.Tabs[^1]);
        Assert.True(column.CanAddTab);
    }
}
