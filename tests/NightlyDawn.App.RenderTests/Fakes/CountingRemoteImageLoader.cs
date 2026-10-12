using NightlyDawn.Core;

namespace NightlyDawn.App.RenderTests.Fakes;

/// <summary>
/// <see cref="IRemoteImageLoader"/> for the avatar render tests: no network, no HTTP, no cache directory.
/// Answers each URL from <see cref="Resolve"/> (a local file path the test wrote, or null = "the loader
/// rejected or failed it"), counts every call, and can be held open with <see cref="Gate"/> so a test can
/// observe the rows <i>while</i> the fetch is pending.
/// </summary>
internal sealed class CountingRemoteImageLoader : IRemoteImageLoader
{
    private readonly List<string> _calls = [];

    public Func<string, string?> Resolve { get; set; } = _ => null;

    public TaskCompletionSource? Gate { get; set; }

    public int CallCount
    {
        get
        {
            lock (_calls)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return _calls.ToArray();
            }
        }
    }

    public async Task<string?> GetOrFetchAsync(string url, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _calls.Add(url);
        }

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Resolve(url);
    }
}
