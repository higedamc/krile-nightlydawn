using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.RenderTests.Fakes;

/// <summary>
/// Deterministic <see cref="ITimelineSourceFactory"/>: hands back a fixed list of notes immediately and never
/// touches a network, a relay, or the filesystem. No artificial delay, unlike <c>SampleTimelineSourceFactory</c> —
/// these tests need the stream to finish before the frame is captured, not a live note two seconds later.
/// </summary>
internal sealed class StaticTimelineSourceFactory(IReadOnlyList<Note> notes) : ITimelineSourceFactory
{
    public ITimelineSource Create(Timeline timeline, Account account) => CreateAnonymous(timeline);

    public ITimelineSource CreateAnonymous(Timeline timeline) => new StaticTimelineSource(notes);

    private sealed class StaticTimelineSource(IReadOnlyList<Note> notes) : ITimelineSource
    {
        public async IAsyncEnumerable<TimelineUpdate> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            foreach (var note in notes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new NoteArrived(note);
            }

            yield return new InitialLoadComplete();
        }

        public Task<IReadOnlyList<Note>> LoadOlderAsync(long beforeCreatedAt, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Note>>([]);
    }
}
