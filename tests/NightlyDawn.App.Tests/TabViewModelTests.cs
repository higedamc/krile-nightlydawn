using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>Exercises <see cref="TabViewModel"/> without Avalonia, same pattern as
/// <see cref="TimelineColumnViewModelTests"/>: <c>postToUi</c> runs inline, so state is observable directly.</summary>
public class TabViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TimelineColumnViewModel NewColumn() =>
        new(new FakeTimelineSourceFactory(), postToUi: action => action());

    [Fact]
    public void AddColumn_AppendsAndSelectsIt()
    {
        var tab = new TabViewModel("Home", NewColumn);

        var first = tab.AddColumn();
        Assert.Same(first, Assert.Single(tab.Columns));
        Assert.Same(first, tab.SelectedColumn);

        var second = tab.AddColumn();
        Assert.Equal([first, second], tab.Columns);
        Assert.Same(second, tab.SelectedColumn); // the newest column becomes selected, not the first.
    }

    [Fact]
    public async Task RemoveColumn_DisposesIt_AndStopsItsSubscription()
    {
        // Required negative control (plan §11.3): drop `column.Dispose()` from RemoveColumn and this test
        // times out waiting for Cancelled instead of completing -- "Dispose was called" alone (without
        // observing the underlying stream actually stop) would not catch that mutation.
        var source = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(source);
        var tab = new TabViewModel("Home", () => new TimelineColumnViewModel(factory, action => action()));
        var column = tab.AddColumn();
        column.Query = "from home";
        column.Subscribe();

        tab.RemoveColumn(column);

        await source.Cancelled.WaitAsync(Timeout);
        Assert.Throws<ObjectDisposedException>(column.Subscribe);
    }

    [Fact]
    public void AddColumn_WiresRequestClose_SoTheColumnCanAskToBeRemoved()
    {
        // Required negative control (plan §11.6 / PR #30 review): RequestClose moved the column's own close
        // button into TimelineColumnView's header row, closing over `column` the same way CreateColumn's
        // actionsFactory closes over its own column. If AddColumn instead closed over the tab's first column
        // (the same class of mutation #30's review caught for actionsFactory), the second column's
        // RequestClose would remove the first one instead -- this asserts it removes itself.
        var tab = new TabViewModel("Home", NewColumn);
        var first = tab.AddColumn();
        var second = tab.AddColumn();

        second.RequestClose!.Invoke();

        Assert.Equal([first], tab.Columns);
    }

    [Fact]
    public void RemoveColumn_WhenItWasSelected_SelectsTheNextRemainingColumn_OrNullIfNoneAreLeft()
    {
        var tab = new TabViewModel("Home", NewColumn);
        var first = tab.AddColumn();
        var second = tab.AddColumn();
        tab.SelectedColumn = first;

        tab.RemoveColumn(first);
        Assert.Same(second, tab.SelectedColumn);

        tab.RemoveColumn(second);
        Assert.Empty(tab.Columns); // plan §11.1: a tab can reach zero columns.
        Assert.Null(tab.SelectedColumn);
    }

    [Fact]
    public void RemoveColumn_WhenItWasNotSelected_LeavesTheSelectionAlone()
    {
        var tab = new TabViewModel("Home", NewColumn);
        var first = tab.AddColumn();
        var second = tab.AddColumn();
        tab.SelectedColumn = second;

        tab.RemoveColumn(first);

        Assert.Same(second, tab.SelectedColumn);
    }

    [Fact]
    public void RemoveColumn_NotInThisTab_IsANoOp()
    {
        var tab = new TabViewModel("Home", NewColumn);
        var column = tab.AddColumn();
        var strayColumn = NewColumn();

        tab.RemoveColumn(strayColumn);

        Assert.Equal([column], tab.Columns);
        Assert.Same(column, tab.SelectedColumn);
    }

    [Fact]
    public async Task Dispose_DisposesEveryColumn()
    {
        var sourceA = new FakeTimelineSource();
        var sourceB = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(sourceA).Enqueue(sourceB);
        var tab = new TabViewModel("Home", () => new TimelineColumnViewModel(factory, action => action()));
        var columnA = tab.AddColumn();
        columnA.Query = "from home";
        columnA.Subscribe();
        var columnB = tab.AddColumn();
        columnB.Query = "from mentions";
        columnB.Subscribe();

        tab.Dispose();

        await sourceA.Cancelled.WaitAsync(Timeout);
        await sourceB.Cancelled.WaitAsync(Timeout);
        Assert.Throws<ObjectDisposedException>(columnA.Subscribe);
        Assert.Throws<ObjectDisposedException>(columnB.Subscribe);
    }

    [Fact]
    public void PropertyChanged_FiresForTitleAndSelectedColumn()
    {
        var tab = new TabViewModel("Home", NewColumn);
        var changed = new List<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tab.Title = "Renamed";
        tab.Title = "Renamed"; // unchanged value: no event.
        tab.AddColumn(); // raises SelectedColumn (set inside AddColumn).

        Assert.Equal(["Title", "SelectedColumn"], changed);
    }
}
