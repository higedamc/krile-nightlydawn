using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>
/// L18 (plan §68): <see cref="ColumnViewModel.IsFocused"/> is a second fact about "which column is selected", and a
/// second fact can drift from the first. <see cref="WorkspaceViewModel.SelectedColumn"/> is the authority and its setter
/// the only writer; this drives every path that moves the selection and asserts, after each step, that the two never
/// disagree: exactly the selected column is focused, every other column -- including ones that were just closed -- is not.
///
/// <para>Negative controls run while writing this (each reverted afterwards): making the <c>SelectedColumn</c> setter
/// skip clearing the previous column turns <see cref="EveryPathThatMovesTheSelection_KeepsIsFocusedAndSelectedColumnInAgreement"/>
/// red on its first selection change; skipping the set on the new one turns it red on the first step.</para>
/// </summary>
public class ColumnFocusTests
{
    private static WorkspaceViewModel NewWorkspace() =>
        new(() => new TimelineViewModel(new FakeTimelineSourceFactory(), postToUi: action => action()));

    private static void AssertAgreement(WorkspaceViewModel workspace, string step, params ColumnViewModel[] closed)
    {
        foreach (var column in workspace.Columns)
        {
            Assert.True(
                column.IsFocused == ReferenceEquals(column, workspace.SelectedColumn),
                $"{step}: column {column.Id} IsFocused={column.IsFocused} but SelectedColumn is {workspace.SelectedColumn?.Id ?? "(null)"}");
        }

        foreach (var column in closed)
        {
            Assert.False(column.IsFocused, $"{step}: closed column {column.Id} still reports IsFocused");
        }
    }

    [Fact]
    public void EveryPathThatMovesTheSelection_KeepsIsFocusedAndSelectedColumnInAgreement()
    {
        var workspace = NewWorkspace();
        var first = workspace.Columns[0];
        AssertAgreement(workspace, "construction");
        Assert.True(first.IsFocused);

        var second = workspace.AddColumn();
        var third = workspace.AddColumn();
        AssertAgreement(workspace, "AddColumn x2");
        Assert.True(third.IsFocused);
        Assert.False(first.IsFocused);

        workspace.SelectedColumn = first;
        AssertAgreement(workspace, "select first by hand");

        workspace.SelectedColumn = second;
        workspace.CloseColumn(third); // closing a column that is NOT selected must not move focus
        AssertAgreement(workspace, "close a non-selected column", third);
        Assert.True(second.IsFocused);

        workspace.CloseColumn(second); // closing the selected one hands the selection (and the focus) on
        AssertAgreement(workspace, "close the selected column", second, third);
        Assert.True(first.IsFocused);

        workspace.AddColumn();
        var beforeRestore = workspace.Columns.ToArray();
        workspace.RestoreLayout(
            [
                new Column("saved-a", [new Timeline("t1", "", "")], SelectedTabId: "t1"),
                new Column("saved-b", [new Timeline("t2", "", "")], SelectedTabId: "t2"),
            ],
            activeColumnId: "saved-b");
        AssertAgreement(workspace, "RestoreLayout", beforeRestore);
        Assert.Equal("saved-b", workspace.SelectedColumn!.Id);
        Assert.True(workspace.SelectedColumn.IsFocused);

        workspace.SelectedColumn = null;
        AssertAgreement(workspace, "clear the selection");
        Assert.All(workspace.Columns, c => Assert.False(c.IsFocused));
    }

    [Fact]
    public void IsFocused_RaisesPropertyChanged_SoABoundTabHeaderRepaints()
    {
        var workspace = NewWorkspace();
        var other = workspace.AddColumn();
        var first = workspace.Columns[0];
        var raised = new List<string?>();
        first.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        workspace.SelectedColumn = first;
        Assert.Contains(nameof(ColumnViewModel.IsFocused), raised);

        raised.Clear();
        workspace.SelectedColumn = other;
        Assert.Contains(nameof(ColumnViewModel.IsFocused), raised);
    }
}
