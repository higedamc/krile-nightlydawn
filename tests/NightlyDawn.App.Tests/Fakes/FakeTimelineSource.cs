using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>Scriptable <see cref="ITimelineSource"/>: tests push updates, complete the stream, or make it fault.</summary>
internal sealed class FakeTimelineSource : ITimelineSource
{
    private readonly Channel<TimelineUpdate> _updates = Channel.CreateUnbounded<TimelineUpdate>();
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When true the stream keeps reading after cancellation, like a source that ignores its token. Used to prove the view model drops late updates itself.</summary>
    public bool IgnoreCancellation { get; init; }

    /// <summary>Thrown after the pushed updates are drained and the writer is completed.</summary>
    public Exception? FaultAfterDrain { get; init; }

    public Task Cancelled => _cancelled.Task;

    public int LoadOlderCalls { get; private set; }

    public void Push(TimelineUpdate update) => _updates.Writer.TryWrite(update);

    public void Complete() => _updates.Writer.Complete();

    public async IAsyncEnumerable<TimelineUpdate> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var registration = cancellationToken.Register(() => _cancelled.TrySetResult());
        var token = IgnoreCancellation ? CancellationToken.None : cancellationToken;

        await foreach (var update in _updates.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            yield return update;
        }

        if (FaultAfterDrain is not null)
        {
            throw FaultAfterDrain;
        }
    }

    public Task<IReadOnlyList<Note>> LoadOlderAsync(long beforeCreatedAt, int limit, CancellationToken cancellationToken = default)
    {
        LoadOlderCalls++;
        return Task.FromResult<IReadOnlyList<Note>>([]);
    }
}

/// <summary>Records every <see cref="Create"/> call and hands out the queued sources in order.</summary>
internal sealed class FakeTimelineSourceFactory : ITimelineSourceFactory
{
    private readonly Queue<ITimelineSource> _sources = new();

    public List<Timeline> AnonymousCalls { get; } = [];

    public List<(Timeline Timeline, Account Account)> AccountCalls { get; } = [];

    public Exception? ThrowOnCreate { get; init; }

    public FakeTimelineSourceFactory Enqueue(ITimelineSource source)
    {
        _sources.Enqueue(source);
        return this;
    }

    public ITimelineSource Create(Timeline timeline, Account account)
    {
        AccountCalls.Add((timeline, account));
        return Next();
    }

    public ITimelineSource CreateAnonymous(Timeline timeline)
    {
        AnonymousCalls.Add(timeline);
        return Next();
    }

    private ITimelineSource Next()
    {
        if (ThrowOnCreate is not null)
        {
            throw ThrowOnCreate;
        }

        return _sources.Dequeue();
    }
}

internal static class TestNotes
{
    public const string Author = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    public static Note Make(int n, long createdAt, string? content = null, NoteKind kind = NoteKind.Text, string? authorPubkey = null) =>
        new(
            Id: n.ToString("x64"),
            AuthorPubkey: authorPubkey ?? Author,
            CreatedAt: createdAt,
            Kind: kind,
            Content: content ?? $"note {n}",
            Tags: []);
}
