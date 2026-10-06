using System.Diagnostics;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>Exercises the column without Avalonia: <c>postToUi</c> runs actions inline, so state is observable directly.</summary>
public class TimelineColumnViewModelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TimelineColumnViewModel NewViewModel(ITimelineSourceFactory factory, int maxNotes = TimelineColumnViewModel.DefaultMaxNotes) =>
        new(factory, postToUi: action => action(), maxNotes);

    [Fact]
    public async Task Subscribe_UsesAnonymousPath_PassesRawQuery_AndStaysLoadingUntilInitialLoadComplete()
    {
        var source = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(source);
        using var vm = NewViewModel(factory);

        vm.Query = "  from home where text contains \"nostr\"  ";
        vm.Subscribe();

        var timeline = Assert.Single(factory.AnonymousCalls);
        Assert.Empty(factory.AccountCalls); // No signer exists yet: the read-only contract path must be the one used.
        Assert.Equal("from home where text contains \"nostr\"", timeline.KqlQuery); // Raw text, trimmed; compiling it is downstream's job (1c/1e-b).
        Assert.Same(timeline, vm.ActiveTimeline);
        Assert.True(vm.IsLoading);

        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100)));
        source.Push(new NoteArrived(TestNotes.Make(2, createdAt: 300)));
        source.Push(new NoteArrived(TestNotes.Make(3, createdAt: 200)));
        await WaitUntil(() => vm.Notes.Count == 3, "three notes shown");

        Assert.True(vm.IsLoading); // Notes alone do not end "loading" (B6): the backlog is not known to be complete.
        Assert.Equal(["note 2", "note 3", "note 1"], vm.Notes.Select(n => n.DisplayContent)); // Newest first.

        source.Push(new InitialLoadComplete());
        await WaitUntil(() => !vm.IsLoading, "loading cleared");
        Assert.Equal("3 notes · live", vm.Status);

        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);
        Assert.Equal("3 notes · stream ended.", vm.Status);
    }

    [Fact]
    public async Task DuplicateNoteIds_AreShownOnce()
    {
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source));
        vm.Query = "from home";
        vm.Subscribe();

        source.Push(new NoteArrived(TestNotes.Make(7, createdAt: 10)));
        source.Push(new NoteArrived(TestNotes.Make(7, createdAt: 10))); // Same event seen on a second relay (B7).
        source.Push(new NoteArrived(TestNotes.Make(8, createdAt: 20)));
        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);

        Assert.Equal(2, vm.Notes.Count);
        Assert.Equal("2 notes · stream ended.", vm.Status);
    }

    [Fact]
    public async Task Resubscribe_CancelsThePreviousStream_ClearsTheColumn_AndDropsLateUpdatesFromIt()
    {
        var first = new FakeTimelineSource { IgnoreCancellation = true }; // A badly behaved source that keeps streaming after cancel.
        var second = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(first).Enqueue(second);
        using var vm = NewViewModel(factory);

        vm.Query = "from home";
        vm.Subscribe();
        first.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100)));
        await WaitUntil(() => vm.Notes.Count == 1, "first note shown");
        var firstCompletion = vm.StreamCompletion;

        vm.Query = "from mentions";
        vm.Subscribe();

        await first.Cancelled.WaitAsync(Timeout); // The old token was cancelled...
        Assert.Empty(vm.Notes); // ...and the column was cleared for the new query.
        Assert.True(vm.IsLoading);
        Assert.Equal(2, factory.AnonymousCalls.Count);

        first.Push(new NoteArrived(TestNotes.Make(2, createdAt: 200))); // ...but this source ignores it. Its update must not leak into the new column.
        first.Push(new InitialLoadComplete());
        first.Complete();
        await firstCompletion.WaitAsync(Timeout);

        Assert.Empty(vm.Notes);
        Assert.True(vm.IsLoading); // The stale InitialLoadComplete did not end the new column's loading state.

        second.Push(new NoteArrived(TestNotes.Make(3, createdAt: 300)));
        second.Push(new InitialLoadComplete());
        await WaitUntil(() => !vm.IsLoading, "second stream loaded");
        Assert.Equal(["note 3"], vm.Notes.Select(n => n.DisplayContent));

        second.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task StreamFault_EndsLoading_AndReportsOnlyTheExceptionType()
    {
        var source = new FakeTimelineSource { FaultAfterDrain = new InvalidOperationException("relay said: do not show this payload") };
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source));
        vm.Query = "from home";
        vm.Subscribe();

        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 1)));
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);

        Assert.False(vm.IsLoading);
        Assert.Equal("Timeline stopped: InvalidOperationException.", vm.Status);
        Assert.DoesNotContain("payload", vm.Status, StringComparison.Ordinal);
        Assert.Single(vm.Notes); // What arrived before the fault stays visible.
    }

    [Fact]
    public void FactoryFailure_IsReportedInStatus_NotThrown()
    {
        var factory = new FakeTimelineSourceFactory { ThrowOnCreate = new FilterParseException("bad kql") };
        using var vm = NewViewModel(factory);
        vm.Query = "from nowhere(";

        vm.Subscribe();

        Assert.False(vm.IsLoading);
        Assert.Equal("Could not open this timeline (FilterParseException).", vm.Status);
        Assert.Empty(vm.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EmptyQuery_DoesNotCreateASource(string query)
    {
        var factory = new FakeTimelineSourceFactory();
        using var vm = NewViewModel(factory);
        vm.Query = query;

        vm.Subscribe();

        Assert.Empty(factory.AnonymousCalls);
        Assert.Empty(factory.AccountCalls);
        Assert.False(vm.IsLoading);
        Assert.Equal("Enter a query first.", vm.Status);
        Assert.Null(vm.ActiveTimeline);
    }

    [Fact]
    public async Task Column_IsBounded_DroppingTheOldestNotes()
    {
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source), maxNotes: 3);
        vm.Query = "from home";
        vm.Subscribe();

        for (var i = 1; i <= 5; i++)
        {
            source.Push(new NoteArrived(TestNotes.Make(i, createdAt: i * 10)));
        }

        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);

        Assert.Equal(["note 5", "note 4", "note 3"], vm.Notes.Select(n => n.DisplayContent));
        Assert.Equal("3 notes · stream ended.", vm.Status);
    }

    [Fact]
    public async Task Dispose_CancelsTheStream_AndBlocksFurtherSubscribes()
    {
        var source = new FakeTimelineSource();
        var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source));
        vm.Query = "from home";
        vm.Subscribe();

        vm.Dispose();

        await source.Cancelled.WaitAsync(Timeout);
        await vm.StreamCompletion.WaitAsync(Timeout);
        Assert.Throws<ObjectDisposedException>(vm.Subscribe);
    }

    [Fact]
    public void PropertyChanged_FiresForQueryStatusAndLoading()
    {
        using var vm = NewViewModel(new FakeTimelineSourceFactory());
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Query = "from home";
        vm.Query = "from home"; // Unchanged value: no event.
        vm.Subscribe(); // Factory has no source queued -> reported as a failure through Status.

        Assert.Equal(["Query", "Status"], changed);
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
