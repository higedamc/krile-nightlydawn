using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.RenderTests.Fakes;

/// <summary>
/// Deterministic <see cref="ITimelineSourceFactory"/> for L5j/L5k's account-wiring render tests (plan §18.2,
/// §18.5): <see cref="CreateAnonymous"/> throws <see cref="AccountRequiredException"/> for a <c>from home</c>
/// query, exactly like the real <c>NostrTimelineSourceFactory</c> does for that source with no account -- but
/// with no network, no KQL compiler, and no relay. <see cref="Create"/> always succeeds and returns
/// <paramref name="accountNotes"/>, so a test can tell the account-bound path actually ran (the notes show up)
/// rather than merely that <see cref="AccountCalls"/> was incremented. The resulting source's
/// <see cref="ITimelineSource.IsAccountBound"/> is derived the same way the real
/// <c>NostrFilterQueryCompiler</c> derives it -- from whether the query text is <c>from home</c> -- so that
/// being signed in and going through <c>Create</c> is not, by itself, enough to look account-bound (the L5j
/// wart this leaf fixes): a column like <c>from kind("1")</c> opened through <c>Create</c> still answers false.
/// </summary>
internal sealed class AccountGatedTimelineSourceFactory(IReadOnlyList<Note> accountNotes) : ITimelineSourceFactory
{
    public List<Account> AccountCalls { get; } = [];

    public ITimelineSource Create(Timeline timeline, Account account)
    {
        AccountCalls.Add(account);
        return new StaticSource(accountNotes, IsAccountBoundQuery(timeline));
    }

    public ITimelineSource CreateAnonymous(Timeline timeline) =>
        IsAccountBoundQuery(timeline)
            ? throw new AccountRequiredException(FilterSourceKind.Home)
            : new StaticSource([], isAccountBound: false);

    private static bool IsAccountBoundQuery(Timeline timeline) =>
        timeline.KqlQuery.Contains("from home", StringComparison.Ordinal);

    private sealed class StaticSource(IReadOnlyList<Note> notes, bool isAccountBound) : ITimelineSource
    {
        public bool IsAccountBound => isAccountBound;

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
