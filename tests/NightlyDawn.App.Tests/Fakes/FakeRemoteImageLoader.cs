using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IRemoteImageLoader"/>: no network, no disk. Records every URL asked for, answers
/// each through <see cref="Resolve"/> (defaulting to "fetch failed" = null), and can be held open with
/// <see cref="Gate"/> so a test can prove two requests shared one fetch -- not merely that the second one
/// found the first one's result in the cache afterwards.
/// </summary>
internal sealed class FakeRemoteImageLoader : IRemoteImageLoader
{
    public List<string> Calls { get; } = [];

    public Func<string, string?> Resolve { get; set; } = _ => null;

    /// <summary>When set, every call awaits this before answering.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<string?> GetOrFetchAsync(string url, CancellationToken cancellationToken = default)
    {
        lock (Calls)
        {
            Calls.Add(url);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Resolve(url);
    }

    public int CallCount
    {
        get
        {
            lock (Calls)
            {
                return Calls.Count;
            }
        }
    }
}
