using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IProfileStore"/>: records every <see cref="PrefetchAsync"/> call, resolves each
/// pubkey through <see cref="ResolveOnPrefetch"/> (defaulting to "nothing found"), and can be gated with a
/// <see cref="TaskCompletionSource"/> so a test can observe the view model's state *while* a prefetch is
/// still pending -- proving it waits, not just that it eventually shows the right thing.
/// </summary>
internal sealed class FakeProfileStore : IProfileStore
{
    private readonly Dictionary<string, Profile?> _cache = new(StringComparer.Ordinal);

    public List<IReadOnlyCollection<string>> PrefetchCalls { get; } = [];

    public Func<string, Profile?>? ResolveOnPrefetch { get; set; }

    /// <summary>When set, every <see cref="PrefetchAsync"/> call awaits this before resolving anything.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public Exception? ThrowOnPrefetch { get; set; }

    public Profile? TryGet(string pubkey) => _cache.TryGetValue(pubkey, out var profile) ? profile : null;

    public async Task PrefetchAsync(IReadOnlyCollection<string> pubkeys, CancellationToken cancellationToken = default)
    {
        PrefetchCalls.Add(pubkeys);

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ThrowOnPrefetch is not null)
        {
            throw ThrowOnPrefetch;
        }

        foreach (var pubkey in pubkeys)
        {
            if (!_cache.ContainsKey(pubkey))
            {
                _cache[pubkey] = ResolveOnPrefetch?.Invoke(pubkey);
            }
        }
    }
}
