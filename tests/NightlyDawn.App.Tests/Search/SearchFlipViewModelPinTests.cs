using System.Diagnostics;
using NightlyDawn.App.Search;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Filters;
using Xunit;

namespace NightlyDawn.App.Tests.Search;

/// <summary>
/// <see cref="SearchFlipViewModel.PinToTab"/> (plan §37.2 item 11, L7b, dispatched in plan §41), exercised the
/// way <see cref="ColumnViewModelTests"/> and <see cref="TimelineViewModelTests"/> already do: no Avalonia,
/// <c>postToUi</c> running inline, a <see cref="FakeTimelineSourceFactory"/> standing in for the relay layer. The
/// render-level fake (<c>StaticTimelineSourceFactory</c> in <c>SearchFlipRenderTests</c>) ignores a tab's query
/// text entirely, so it cannot prove the pinned tab's generated KQL is right -- that is this file's job; the
/// render tests cover the button/click/focus/selection plumbing around it instead.
/// </summary>
public class SearchFlipViewModelPinTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly FilterCompiler Compiler = new();

    [Fact]
    public async Task InCurrentTabMode_AndsTheKeywordExpressionOntoTheOriginalTabQuery_SelectsTheNewTab_AndClosesThePanel()
    {
        var factory = new FakeTimelineSourceFactory();
        var originalSource = new FakeTimelineSource();
        factory.Enqueue(originalSource);
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var search = new SearchFlipViewModel(() => column.SelectedTab, () => column, Compiler);

        var original = column.AddTab();
        original.Query = "from kind(\"1\") where (created_at > 250)"; // A constraint the kind("1") source does not already imply, so dropping it is observable.
        original.Subscribe();
        originalSource.Push(new NoteArrived(TestNotes.Make(1, 100, content: "alpha one")));
        originalSource.Push(new NoteArrived(TestNotes.Make(2, 200, content: "beta two")));
        originalSource.Push(new NoteArrived(TestNotes.Make(3, 300, content: "alpha gamma")));
        originalSource.Push(new InitialLoadComplete());
        await WaitUntil(() => !original.IsLoading, "original tab finished its initial load");

        search.Open();
        search.Text = "alpha -gamma"; // As-you-type commits in CurrentTab mode (plan §37.2 item 4).
        Assert.Equal(["alpha one"], search.SearchResult!.Notes.Select(n => n.DisplayContent));

        var pinnedSource = new FakeTimelineSource();
        factory.Enqueue(pinnedSource); // Dequeued by the pinned tab's own Subscribe() call, inside PinToTab.

        search.PinToTab();

        Assert.Null(search.PinError);
        Assert.False(search.IsOpen); // Plan §41's brief: pin closes the flip, it does not RewindStack like the fork.
        Assert.Equal(2, column.Tabs.Count);
        var pinned = column.Tabs[1];
        Assert.Same(pinned, column.SelectedTab);

        // The AND composition (plan §41 item ②): the original tab's own where, ANDed with the OR/exclusion
        // keyword expression KeywordMatcher.Parse("alpha -gamma") would itself evaluate -- checked by compiling
        // the generated text and replaying it over fixture notes, rather than comparing text. created_at > 250
        // is not implied by the kind("1") source, so dropping the AND (not just the keyword half) is observable:
        // note 4 matches the keyword but is older than the original where allows.
        var compiled = Compiler.Compile(Compiler.Parse(pinned.Query));
        var matcher = KeywordMatcher.Parse("alpha -gamma");
        Assert.True(Accepts(compiled, TestNotes.Make(1, 300, content: "alpha one")));
        Assert.False(Accepts(compiled, TestNotes.Make(2, 300, content: "beta two"))); // created_at ok, keyword fails.
        Assert.False(Accepts(compiled, TestNotes.Make(3, 300, content: "alpha gamma"))); // keyword excludes it.
        Assert.False(Accepts(compiled, TestNotes.Make(4, 100, content: "alpha one"))); // keyword ok, but original where (created_at > 250) now fails.
        Assert.True(matcher.IsMatch("alpha one") && !matcher.IsMatch("beta two") && !matcher.IsMatch("alpha gamma")); // Sanity: the live rule agrees.
    }

    [Fact]
    public async Task InCurrentTabMode_WhenTheOriginalTabHasNoWhereClause_PinsTheKeywordExpressionAlone()
    {
        var factory = new FakeTimelineSourceFactory();
        var originalSource = new FakeTimelineSource();
        factory.Enqueue(originalSource);
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var search = new SearchFlipViewModel(() => column.SelectedTab, () => column, Compiler);

        var original = column.AddTab();
        original.Query = "from kind(\"1\") where ()"; // FilterAst.Where is null after this parses.
        original.Subscribe();
        originalSource.Push(new NoteArrived(TestNotes.Make(1, 100, content: "alpha one")));
        originalSource.Push(new InitialLoadComplete());
        await WaitUntil(() => !original.IsLoading, "original tab finished its initial load");

        search.Open();
        search.Text = "alpha";
        Assert.NotNull(search.SearchResult);

        factory.Enqueue(new FakeTimelineSource());
        search.PinToTab();

        Assert.Null(search.PinError);
        var pinned = column.Tabs[1];
        var compiled = Compiler.Compile(Compiler.Parse(pinned.Query));
        Assert.True(Accepts(compiled, TestNotes.Make(5, 500, content: "alpha one")));
        Assert.False(Accepts(compiled, TestNotes.Make(6, 600, content: "beta two")));
    }

    [Fact]
    public void InQueryMode_PinsTheTypedExpressionVerbatim()
    {
        var factory = new FakeTimelineSourceFactory();
        var originalSource = new FakeTimelineSource();
        factory.Enqueue(originalSource);
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var search = new SearchFlipViewModel(() => column.SelectedTab, () => column, Compiler);

        var original = column.AddTab();
        original.Query = "from kind(\"1\") where ()";
        original.Subscribe();

        search.Open();
        search.Text = "?from kind(\"1\") where (text contains \"alpha\")";
        Assert.True(search.IsQueryMode);
        Assert.Null(search.ErrorText);
        Assert.Null(search.SearchResult); // ? mode commits only on Enter/mode change (plan §37.2 item 4), not as-you-type.

        search.OnEnterKeyDown();
        Assert.NotNull(search.SearchResult);

        factory.Enqueue(new FakeTimelineSource());
        search.PinToTab();

        Assert.Null(search.PinError);
        Assert.Equal("from kind(\"1\") where (text contains \"alpha\")", column.Tabs[1].Query);
    }

    [Fact]
    public void WhenTheColumnIsFull_SetsPinErrorAndLeavesTheColumnAndThePanelUntouched()
    {
        var factory = new FakeTimelineSourceFactory();
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var search = new SearchFlipViewModel(() => column.SelectedTab, () => column, Compiler);

        for (var i = 0; i < AppState.MaxTabsPerColumn; i++)
        {
            column.AddTab();
        }

        Assert.False(column.CanAddTab);
        search.Open();
        search.Text = "alpha"; // The selected (last-added) tab has no notes, but ShowResult only needs a tab, not notes.
        Assert.NotNull(search.SearchResult);

        search.PinToTab();

        Assert.Contains("already holds", search.PinError);
        Assert.Equal(AppState.MaxTabsPerColumn, column.Tabs.Count);
        Assert.True(search.IsOpen); // A failed pin must not also close the panel out from under the person.
    }

    [Fact]
    public void WhenNoColumnIsSelected_SetsPinError()
    {
        var factory = new FakeTimelineSourceFactory();
        var tab = new TimelineViewModel(factory, action => action()); // Never subscribed: PinToTab fails on the missing column before BuildPinnedQuery would ever read Query.
        var search = new SearchFlipViewModel(() => tab, () => null, Compiler);

        search.Open();
        search.Text = "alpha";
        Assert.NotNull(search.SearchResult);

        search.PinToTab();

        Assert.Equal("No column is selected to pin into.", search.PinError);
    }

    [Fact]
    public void WhenTheCurrentTabHasNoQueryOfItsOwn_ReportsThroughPinError_InsteadOfThrowing()
    {
        var factory = new FakeTimelineSourceFactory();
        var column = new ColumnViewModel(() => new TimelineViewModel(factory, action => action()));
        var search = new SearchFlipViewModel(() => column.SelectedTab, () => column, Compiler);

        column.AddTab(); // Query left at its default "" -- never subscribed.
        search.Open();
        search.Text = "alpha";
        Assert.NotNull(search.SearchResult);

        search.PinToTab();

        Assert.Equal("Could not pin: The current tab has no query of its own to combine with.", search.PinError);
        Assert.Single(column.Tabs); // Nothing was added: the failure is caught before AddTab is called.
    }

    private static bool Accepts(CompiledFilter compiled, Note note) =>
        (compiled.RelayFilter.Kinds?.Contains((int)note.Kind) ?? true) && (compiled.LocalPredicate?.Invoke(note) ?? true);

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
