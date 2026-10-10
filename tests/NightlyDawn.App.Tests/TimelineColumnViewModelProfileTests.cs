using System.Diagnostics;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>Plan §8.1: a row's author label comes from whatever is cached when the row is built, live notes
/// wait on their author's prefetch before they are inserted, and a profile-lookup failure never stops the
/// note stream. Each with the negative control that proves it.</summary>
public class TimelineColumnViewModelProfileTests
{
    private const string Author = TestNotes.Author;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static TimelineColumnViewModel NewViewModel(ITimelineSourceFactory factory, IProfileStore? profileStore) =>
        new(factory, postToUi: action => action(), profileStore: profileStore);

    [Fact]
    public async Task AlreadyCachedProfile_IsUsedForTheRowsAuthorLabel()
    {
        var profileStore = new FakeProfileStore { ResolveOnPrefetch = _ => new Profile(Author, DisplayName: "Fizzy Bee") };
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source), profileStore);

        vm.Query = "from home";
        vm.Subscribe();
        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100)));
        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);

        var row = Assert.Single(vm.Notes);
        Assert.Equal("Fizzy Bee (abcdef01…)", row.AuthorLabel);
    }

    [Fact]
    public async Task NoProfileStore_FallsBackToThePubkeyPrefix_SameAsBeforeThisLeaf()
    {
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source), profileStore: null);

        vm.Query = "from home";
        vm.Subscribe();
        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100)));
        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);

        var row = Assert.Single(vm.Notes);
        Assert.Equal("abcdef01…", row.AuthorLabel);
    }

    [Fact]
    public async Task LiveNote_WaitsForItsAuthorsPrefetch_BeforeTheRowIsInserted()
    {
        var profileStore = new FakeProfileStore
        {
            Gate = new TaskCompletionSource(),
            ResolveOnPrefetch = _ => new Profile(Author, DisplayName: "Resolved Live"),
        };
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source), profileStore);
        vm.Query = "from home";
        vm.Subscribe();
        source.Push(new InitialLoadComplete()); // Loading ends immediately; the note below arrives "live".

        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100)));
        await WaitUntil(() => profileStore.PrefetchCalls.Count == 1, "prefetch to be requested");

        // The mutation this catches: building the row from TryGet() before awaiting PrefetchAsync. If that
        // happened, the row would already be here (with the fallback label) instead of still being held back.
        await Task.Delay(50);
        Assert.Empty(vm.Notes);

        profileStore.Gate.SetResult();
        await WaitUntil(() => vm.Notes.Count == 1, "the row to appear once the prefetch resolves");
        Assert.Equal("Resolved Live (abcdef01…)", vm.Notes[0].AuthorLabel);
    }

    [Fact]
    public async Task PrefetchFailure_DoesNotStopTheStream_AndFallsBackToThePubkeyPrefix()
    {
        var profileStore = new FakeProfileStore { ThrowOnPrefetch = new InvalidOperationException("relay offline") };
        var source = new FakeTimelineSource();
        using var vm = NewViewModel(new FakeTimelineSourceFactory().Enqueue(source), profileStore);
        vm.Query = "from home";
        vm.Subscribe();

        source.Push(new NoteArrived(TestNotes.Make(1, createdAt: 100)));
        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(Timeout);

        var row = Assert.Single(vm.Notes);
        Assert.Equal("abcdef01…", row.AuthorLabel); // Unresolved, not crashed.
        Assert.Equal("1 notes · stream ended.", vm.Status); // Not "Timeline stopped: ...".
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
