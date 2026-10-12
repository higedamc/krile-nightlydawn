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

        // Held until InitialLoadComplete, not shown note-by-note (plan §8.1-2: one batched prefetch for the
        // whole initial load, not one REQ per arriving note).
        await Task.Delay(50);
        Assert.Empty(vm.Notes);
        Assert.True(vm.IsLoading); // Notes alone do not end "loading" (B6): the backlog is not known to be complete.

        source.Push(new InitialLoadComplete());
        await WaitUntil(() => !vm.IsLoading, "loading cleared");
        Assert.Equal(["note 2", "note 3", "note 1"], vm.Notes.Select(n => n.DisplayContent)); // Newest first.
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
        first.Push(new InitialLoadComplete()); // Ends loading with an empty backlog; the note below arrives "live".
        await WaitUntil(() => !vm.IsLoading, "first stream loaded");
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
        // FilterParseException is a NightlyDawnException: its own Message is written for a user, so the status
        // line quotes it instead of just naming the type (plan §18.2 item 3) -- unlike an exception type nobody
        // anticipated, which still gets the type-name-only fallback (see the InvalidOperationException case in
        // PropertyChanged_FiresForQueryStatusAndLoading below).
        var factory = new FakeTimelineSourceFactory { ThrowOnCreate = new FilterParseException("bad kql") };
        using var vm = NewViewModel(factory);
        vm.Query = "from nowhere(";

        vm.Subscribe();

        Assert.False(vm.IsLoading);
        Assert.Equal("Could not open this timeline: bad kql", vm.Status);
        Assert.Empty(vm.Notes);
        Assert.False(vm.NeedsAccountRetry); // FilterParseException is not AccountRequiredException: nothing to retry once a key appears.
    }

    [Fact]
    public void AccountRequiredException_QuotesItsOwnMessage_AndArmsNeedsAccountRetry()
    {
        var factory = new FakeTimelineSourceFactory { ThrowOnCreate = new AccountRequiredException(FilterSourceKind.Home) };
        using var vm = NewViewModel(factory);
        vm.Query = "from home";

        vm.Subscribe();

        Assert.False(vm.IsLoading);
        Assert.Equal("Could not open this timeline: 'from home' needs a signed-in account.", vm.Status);
        Assert.True(vm.NeedsAccountRetry);
        Assert.False(vm.IsAccountBound);
    }

    [Fact]
    public async Task Subscribe_WithAnActiveSigner_UsesTheAccountPath_AndSetsIsAccountBound()
    {
        var source = new FakeTimelineSource { IsAccountBound = true }; // `from home` is an account-bound source.
        var factory = new FakeTimelineSourceFactory().Enqueue(source);
        var signer = new SignerDescriptor("ab".PadRight(64, '1'), SignerKind.Nip49Local);
        var keyStore = new FakeKeyStore { ActiveSigner = signer };
        using var vm = new TimelineColumnViewModel(factory, postToUi: action => action(), keyStore: keyStore);
        vm.Query = "from home";

        vm.Subscribe();

        Assert.Empty(factory.AnonymousCalls);
        var call = Assert.Single(factory.AccountCalls);
        Assert.Equal(signer.Pubkey, call.Account.Pubkey);
        Assert.True(vm.IsAccountBound);
        Assert.False(vm.NeedsAccountRetry);

        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);
    }

    /// <summary>The L5j wart this leaf fixes (plan §18.5): being signed in routes <em>every</em> query through
    /// <see cref="ITimelineSourceFactory.Create"/>, account-bound or not. <see cref="TimelineColumnViewModel.IsAccountBound"/>
    /// must still answer false for a query like <c>from kind("1")</c> that never consumes the account, or
    /// <see cref="WorkspaceViewModel.NotifyAccountLocked"/> would wrongly resubscribe (and clear) a column that
    /// has nothing to do with the account that just locked.</summary>
    [Fact]
    public async Task Subscribe_WithAnActiveSigner_ButAnAccountIndifferentQuery_LeavesIsAccountBoundFalse()
    {
        var source = new FakeTimelineSource(); // IsAccountBound defaults to false: this source never touches the account.
        var factory = new FakeTimelineSourceFactory().Enqueue(source);
        var signer = new SignerDescriptor("ab".PadRight(64, '1'), SignerKind.Nip49Local);
        var keyStore = new FakeKeyStore { ActiveSigner = signer };
        using var vm = new TimelineColumnViewModel(factory, postToUi: action => action(), keyStore: keyStore);
        vm.Query = "from kind(\"1\")";

        vm.Subscribe();

        var call = Assert.Single(factory.AccountCalls); // Still routed through Create() -- a signer is active.
        Assert.Equal(signer.Pubkey, call.Account.Pubkey);
        Assert.False(vm.IsAccountBound); // ...but the compiled query itself never bound to it.

        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);
    }

    [Fact]
    public void Subscribe_WithAKeyStoreButNoActiveSigner_FallsBackToAnonymous()
    {
        var source = new FakeTimelineSource();
        var factory = new FakeTimelineSourceFactory().Enqueue(source);
        var keyStore = new FakeKeyStore(); // No ActiveSigner set: GetActiveSignerAsync throws SignerUnavailableException, same as a locked/no-key store.
        using var vm = new TimelineColumnViewModel(factory, postToUi: action => action(), keyStore: keyStore);
        vm.Query = "from user(\"npub1...\")";

        vm.Subscribe();

        var timeline = Assert.Single(factory.AnonymousCalls);
        Assert.Empty(factory.AccountCalls);
        Assert.Equal(timeline, vm.ActiveTimeline);
        Assert.False(vm.IsAccountBound);
    }

    /// <summary>The one real race the account lookup introduced (plan §18.2 item 2): a second
    /// <see cref="TimelineColumnViewModel.Subscribe"/> must win over a first one whose account lookup is still
    /// in flight when it finally resolves. <see cref="Fakes.FakeKeyStore.ActiveSignerDelay"/> is a one-shot, so
    /// generation 1's call consumes it (and blocks on it) while generation 2's call falls through to the
    /// already-set <see cref="Fakes.FakeKeyStore.ActiveSigner"/> and resolves immediately -- without the
    /// generation guard inside <c>Post</c>, generation 1's eventual, late success would overwrite generation 2's
    /// <see cref="TimelineColumnViewModel.ActiveTimeline"/>/<see cref="TimelineColumnViewModel.IsAccountBound"/>
    /// state, or call the factory a second time, even though the user had already moved on.</summary>
    [Fact]
    public async Task LateAccountResolution_FromASupersededSubscribe_IsDropped()
    {
        var staleSigner = new SignerDescriptor("ab".PadRight(64, '1'), SignerKind.Nip49Local);
        var delay = new TaskCompletionSource<SignerDescriptor>();
        var keyStore = new FakeKeyStore { ActiveSignerDelay = delay };
        var source = new FakeTimelineSource { IsAccountBound = true }; // `from home` is an account-bound source.
        var factory = new FakeTimelineSourceFactory().Enqueue(source); // Only one source queued: generation 1 must never reach the factory at all.
        using var vm = new TimelineColumnViewModel(factory, postToUi: action => action(), keyStore: keyStore);

        vm.Query = "from home";
        vm.Subscribe(); // Generation 1: suspends inside ResolveAccountAsync, waiting on `delay`.
        var staleCompletion = vm.StreamCompletion;
        Assert.False(vm.IsAccountBound); // Still resolving -- nothing decided yet.

        keyStore.ActiveSigner = staleSigner; // ActiveSignerDelay was already consumed by generation 1's call, so generation 2's call reads this instead.
        vm.Subscribe(); // Generation 2: resolves inline (an already-completed task), wins the account path synchronously.

        Assert.True(vm.IsAccountBound);
        var secondTimeline = vm.ActiveTimeline;
        Assert.Single(factory.AccountCalls);

        delay.SetResult(staleSigner); // Generation 1's lookup finally completes, long after generation 2 already won.
        await staleCompletion.WaitAsync(Timeout);

        Assert.Same(secondTimeline, vm.ActiveTimeline); // Untouched by generation 1's late arrival.
        Assert.Single(factory.AccountCalls); // Generation 1 never got far enough to call the factory a second time.
        Assert.Empty(factory.AnonymousCalls);
        Assert.True(vm.IsAccountBound);
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

    /// <summary>Integration-level companion to <c>BufferCappedTests</c> below: even when the stream ends
    /// before <see cref="InitialLoadComplete"/> ever arrives, the column that was buffered mid-load is still
    /// flushed, sorted, and correctly bounded.</summary>
    [Fact]
    public async Task Column_IsBounded_EvenWhenInitialLoadCompleteNeverArrives()
    {
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source), maxNotes: 3);
        vm.Query = "from home";
        vm.Subscribe();

        for (var i = 1; i <= 5; i++)
        {
            source.Push(new NoteArrived(TestNotes.Make(i, createdAt: i * 10)));
        }

        source.Complete(); // No InitialLoadComplete: the stream just ends mid-load.
        await vm.StreamCompletion.WaitAsync(Timeout);

        Assert.Equal(["note 5", "note 4", "note 3"], vm.Notes.Select(n => n.DisplayContent));
    }

    /// <summary>The bound itself, not just the column it eventually produces: offering far more than
    /// <c>maxNotes</c> items must never grow the buffer past that cap at any point, since this buffer holds
    /// everything a relay sends for as long as it withholds <see cref="InitialLoadComplete"/> -- deliberately
    /// or otherwise. (A VM-level test using <c>Notes.Count</c> cannot tell this apart from "capped only at
    /// flush time," because <see cref="AddNote"/> trims the final column regardless; this calls the buffering
    /// step directly.)</summary>
    [Fact]
    public void BufferCapped_NeverExceedsMaxNotes_RegardlessOfHowManyNotesAreOffered()
    {
        var buffer = new List<Note>();

        for (var i = 1; i <= 1000; i++)
        {
            TimelineColumnViewModel.BufferCapped(buffer, TestNotes.Make(i, createdAt: i), maxNotes: 3);
            Assert.True(buffer.Count <= 3, $"buffer grew to {buffer.Count} after {i} notes, cap is 3");
        }

        Assert.Equal([1000, 999, 998], buffer.Select(n => n.CreatedAt));
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
