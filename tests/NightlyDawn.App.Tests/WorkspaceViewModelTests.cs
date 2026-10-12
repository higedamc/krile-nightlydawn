using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>Exercises <see cref="WorkspaceViewModel"/> without Avalonia, same pattern as
/// <see cref="ColumnViewModelTests"/>. Added in L9-2 (plan §34) for the producer-side bound: before it, the store
/// was the only owner of <see cref="AppState.MaxColumns"/>, and a 33rd column made every later save of the whole
/// layout fail silently for the rest of the session.</summary>
public class WorkspaceViewModelTests
{
    private static WorkspaceViewModel NewWorkspace() =>
        new(() => new TimelineViewModel(new FakeTimelineSourceFactory(), postToUi: action => action()));

    [Fact]
    public void AddColumn_StopsAtTheContractBound_AndSaysSoThroughCanAddColumn()
    {
        var workspace = NewWorkspace();
        var canAddChanges = 0;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceViewModel.CanAddColumn))
            {
                canAddChanges++;
            }
        };

        // Bounded by the constant, not by CanAddColumn, so a mutation that makes CanAddColumn always true fails the
        // assertion below instead of looping forever.
        for (var i = 1; i < AppState.MaxColumns; i++)
        {
            workspace.AddColumn();
        }

        Assert.False(workspace.CanAddColumn);

        // Exactly the store's bound, so a layout built entirely through this class is always one SaveAsync accepts.
        Assert.Equal(AppState.MaxColumns, workspace.Columns.Count);
        Assert.Equal(AppState.MaxColumns - 1, canAddChanges); // One notification per add: the bound button can follow.

        var ex = Assert.Throws<InvalidOperationException>(() => workspace.AddColumn());
        Assert.Contains(nameof(AppState.MaxColumns), ex.Message, StringComparison.Ordinal);
        Assert.Equal(AppState.MaxColumns, workspace.Columns.Count); // The refused add changed nothing.

        // Closing one re-opens the door: the bound is about the current count, not a one-way latch.
        workspace.CloseColumn(workspace.Columns[^1]);
        Assert.True(workspace.CanAddColumn);
        workspace.AddColumn();
        Assert.Equal(AppState.MaxColumns, workspace.Columns.Count);
    }

    [Fact]
    public void CaptureLayout_AtTheBound_IsAStateTheContractAccepts()
    {
        var workspace = NewWorkspace();
        for (var i = 1; i < AppState.MaxColumns; i++)
        {
            var column = workspace.AddColumn();
            for (var j = 1; j < AppState.MaxTabsPerColumn; j++)
            {
                column.AddTab();
            }
        }

        Assert.False(workspace.CanAddColumn);
        Assert.All(workspace.Columns.Skip(1), c => Assert.False(c.CanAddTab));

        var state = workspace.CaptureLayout(new Settings());

        // The same check the store runs on write (AppStateShape.Check); a full workspace must pass it with zero
        // problems, or the bounds in the view models and the bounds in the contract have drifted apart.
        Assert.Equal(AppState.MaxColumns, state.Columns.Count);
        Assert.All(state.Columns.Skip(1), c => Assert.Equal(AppState.MaxTabsPerColumn, c.Tabs.Count));
    }
}
