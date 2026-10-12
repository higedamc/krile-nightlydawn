using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="IAppStateStore"/> for <c>LayoutPersistenceTests</c>: answers <see cref="LoadAsync"/> from
/// <see cref="LoadResult"/> (or throws <see cref="ThrowOnLoad"/>), records every <see cref="SaveAsync"/> in
/// <see cref="Saved"/> (after throwing <see cref="ThrowOnSave"/> if set, or after <see cref="SaveGate"/> completes
/// if set -- the "disk that never answers" the close-flush timeout is for).
/// </summary>
internal sealed class FakeAppStateStore : IAppStateStore
{
    private readonly List<AppState> _saved = [];

    public AppState LoadResult { get; set; } = AppState.Default;

    public Exception? ThrowOnLoad { get; set; }

    public Exception? ThrowOnSave { get; set; }

    public TaskCompletionSource? SaveGate { get; set; }

    public int LoadCalls { get; private set; }

    public IReadOnlyList<AppState> Saved
    {
        get
        {
            lock (_saved)
            {
                return _saved.ToArray();
            }
        }
    }

    public Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        LoadCalls++;
        return ThrowOnLoad is { } ex ? Task.FromException<AppState>(ex) : Task.FromResult(LoadResult);
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
    {
        if (SaveGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ThrowOnSave is { } ex)
        {
            throw ex;
        }

        lock (_saved)
        {
            _saved.Add(state);
        }
    }
}
