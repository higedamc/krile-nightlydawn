using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// Stand-in <see cref="ITimelineSourceFactory"/> used until the relay-backed source is wired in 1f.
/// Every note it produces is tagged "[sample]" so nobody mistakes it for relay data. It exercises the
/// same three UI states a real source does: backlog, <see cref="InitialLoadComplete"/>, then a live note.
/// </summary>
public sealed class SampleTimelineSourceFactory(TimeSpan? liveNoteDelay = null) : ITimelineSourceFactory
{
    public static readonly TimeSpan DefaultLiveNoteDelay = TimeSpan.FromSeconds(2);

    private readonly TimeSpan _liveNoteDelay = liveNoteDelay ?? DefaultLiveNoteDelay;

    public ITimelineSource Create(Timeline timeline, Account account) => CreateAnonymous(timeline);

    public ITimelineSource CreateAnonymous(Timeline timeline) => new SampleTimelineSource(timeline, _liveNoteDelay);

    internal sealed class SampleTimelineSource(Timeline timeline, TimeSpan liveNoteDelay) : ITimelineSource
    {
        public const int BacklogCount = 3;
        private const string SampleAuthor = "0000000000000000000000000000000000000000000000000000000000000000";

        public async IAsyncEnumerable<TimelineUpdate> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var i = 0; i < BacklogCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new NoteArrived(MakeNote(i, now - (BacklogCount - i) * 60, $"[sample] backlog note {i + 1} for query: {timeline.KqlQuery}"));
            }

            yield return new InitialLoadComplete();

            await Task.Delay(liveNoteDelay, cancellationToken).ConfigureAwait(false);
            yield return new NoteArrived(MakeNote(BacklogCount, now, "[sample] live note - the relay-backed source arrives with leaf 1f"));
        }

        public Task<IReadOnlyList<Note>> LoadOlderAsync(long beforeCreatedAt, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Note>>([]);

        private static Note MakeNote(int index, long createdAt, string content) =>
            new(
                Id: index.ToString("x64"),
                AuthorPubkey: SampleAuthor,
                CreatedAt: createdAt,
                Kind: NoteKind.Text,
                Content: content,
                Tags: []);
    }
}
