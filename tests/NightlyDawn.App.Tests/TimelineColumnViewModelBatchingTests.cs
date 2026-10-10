// Lead review probe for PR #22 (L2). Drop into tests/NightlyDawn.App.Tests/ as-is.
//
// Measured on 0b057799 (2026-10-10):
//   PROBE notes=20 prefetchCalls=20 distinctBatchSizes=[1] rows=20 elapsedMs=1032 perCallLatencyMs=50
// i.e. one profile REQ per arriving note, fully serialized inside the stream pump.
//
// After the fix, the initial load must produce exactly one PrefetchAsync call carrying every distinct
// author, and the elapsed time must be ~one latency, not N.
using System.Diagnostics;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

public class TimelineColumnViewModelBatchingTests
{
    private sealed class CountingProfileStore(TimeSpan latency) : IProfileStore
    {
        private readonly Dictionary<string, Profile?> _cache = new(StringComparer.Ordinal);
        public List<IReadOnlyCollection<string>> PrefetchCalls { get; } = [];

        public Profile? TryGet(string pubkey) => _cache.TryGetValue(pubkey, out var p) ? p : null;

        public async Task PrefetchAsync(IReadOnlyCollection<string> pubkeys, CancellationToken ct = default)
        {
            lock (PrefetchCalls) { PrefetchCalls.Add(pubkeys); }
            await Task.Delay(latency, ct).ConfigureAwait(false); // stands in for "wait for EOSE from every relay"
            foreach (var p in pubkeys)
            {
                if (!_cache.ContainsKey(p)) { _cache[p] = new Profile(p, DisplayName: "n" + p[..4]); }
            }
        }
    }

    private static string Author(int i) => i.ToString("x2") + new string('a', 62);

    /// <summary>
    /// Plan §8.1-2 at the level the contract states it ("a 200-author column must not issue 200 of those"):
    /// the whole initial load is one batched query, not one per row. The mutation this catches is prefetching
    /// inside the per-update loop -- which is what 0b057799 did.
    /// </summary>
    [Fact]
    public async Task InitialLoad_WithNDistinctAuthors_IssuesExactlyOneBatchedPrefetch()
    {
        const int n = 20;
        var latency = TimeSpan.FromMilliseconds(50);
        var store = new CountingProfileStore(latency);
        var source = new FakeTimelineSource();
        using var vm = new TimelineColumnViewModel(
            new FakeTimelineSourceFactory().Enqueue(source),
            postToUi: action => action(),
            profileStore: store);

        vm.Query = "from home";
        vm.Subscribe();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++)
        {
            source.Push(new NoteArrived(TestNotes.Make(i + 1, createdAt: 100 + i, authorPubkey: Author(i))));
        }

        source.Push(new InitialLoadComplete());
        source.Complete();
        await vm.StreamCompletion.WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        Assert.Equal(n, vm.Notes.Count);
        var call = Assert.Single(store.PrefetchCalls);
        Assert.Equal(n, call.Count);

        // Serialization guard: N round trips would be >= n * latency. One batch is ~1 * latency.
        Assert.True(sw.Elapsed < latency * (n / 2.0),
            $"initial load took {sw.ElapsedMilliseconds} ms for {n} notes at {latency.TotalMilliseconds} ms/query -- the prefetch is still serialized per note.");

        // Every row got its resolved label, not the hex fallback.
        Assert.All(vm.Notes, row => Assert.Contains(" (", row.AuthorLabel));
    }
}
