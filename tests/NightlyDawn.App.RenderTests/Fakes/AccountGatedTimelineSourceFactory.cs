using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.RenderTests.Fakes;

/// <summary>
/// Deterministic <see cref="ITimelineSourceFactory"/> for L5j's account-wiring render tests (plan §18.2):
/// <see cref="CreateAnonymous"/> throws <see cref="AccountRequiredException"/> for a <c>from home</c> query,
/// exactly like the real <c>NostrTimelineSourceFactory</c> does for that source with no account -- but with no
/// network, no KQL compiler, and no relay. <see cref="Create"/> always succeeds and returns
/// <paramref name="accountNotes"/>, so a test can tell the account-bound path actually ran (the notes show up)
/// rather than merely that <see cref="AccountCalls"/> was incremented.
/// </summary>
internal sealed class AccountGatedTimelineSourceFactory(IReadOnlyList<Note> accountNotes) : ITimelineSourceFactory
{
    public List<Account> AccountCalls { get; } = [];

    public ITimelineSource Create(Timeline timeline, Account account)
    {
        AccountCalls.Add(account);
        return new StaticSource(accountNotes);
    }

    public ITimelineSource CreateAnonymous(Timeline timeline) =>
        timeline.KqlQuery.Contains("from home", StringComparison.Ordinal)
            ? throw new AccountRequiredException(FilterSourceKind.Home)
            : new StaticSource([]);

    private sealed class StaticSource(IReadOnlyList<Note> notes) : ITimelineSource
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
