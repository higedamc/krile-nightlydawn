using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NightlyDawn.App.Composing;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// One timeline column: a raw KQL query, a bounded newest-first list of <see cref="NoteRow"/>s, and the
/// loading state. Subscribing resolves the active account from <see cref="IKeyStore"/> (if one was given and
/// a signer is active), then asks the <see cref="ITimelineSourceFactory"/> for an account-bound source via
/// <see cref="ITimelineSourceFactory.Create"/>, or an anonymous (read-only, no signer) one via
/// <see cref="ITimelineSourceFactory.CreateAnonymous"/> when no account is active -- and consumes its
/// <see cref="ITimelineSource.StreamAsync"/> on a background task. Every state change is marshalled through
/// <c>postToUi</c>, so the class has no Avalonia dependency and is unit-tested with a synchronous poster.
/// "Loading" ends on <see cref="InitialLoadComplete"/> (B6/B9), not on the first note.
///
/// <para>Implements <see cref="IPublishedNoteSink"/> (plan §10.1-4) so a successful post/reply/quote/repost
/// from <see cref="ComposeViewModel"/> or <see cref="NoteRowActions"/> lands here the same way a relay-echoed
/// note would, without waiting for that echo.</para>
/// </summary>
public sealed class TimelineViewModel : INotifyPropertyChanged, IDisposable, IPublishedNoteSink
{
    public const int DefaultMaxNotes = 500;
    public const string IdleStatus = "Enter a query and press Subscribe.";

    private readonly ITimelineSourceFactory _factory;
    private readonly Action<Action> _postToUi;
    private readonly int _maxNotes;
    private readonly IProfileStore? _profileStore;
    private readonly Func<Note, NoteRowActions?>? _actionsFactory;
    private readonly IKeyStore? _keyStore;
    private readonly HashSet<string> _knownIds = new(StringComparer.Ordinal);

    private CancellationTokenSource? _streamCts;
    private int _generation;
    private string _query = string.Empty;
    private string _status = IdleStatus;
    private bool _isLoading;
    private bool _disposed;

    /// <param name="profileStore">Resolves author display names (plan §8). Null skips profile lookups entirely --
    /// rows show the pubkey-prefix fallback, same as before this leaf.</param>
    /// <param name="actionsFactory">Builds the per-row publish handle (plan §10.1-2). Null leaves every
    /// row's <see cref="NoteRow.Actions"/> null -- the same as before this leaf, and the designer/sample path.</param>
    /// <param name="keyStore">Resolved once per <see cref="Subscribe"/> call for the active account (L5j, plan
    /// §18.2). Null (the default, and the designer/sample path) means every subscription is anonymous, same as
    /// before this leaf -- <see cref="IKeyStore"/> is the single source of truth for who is signed in; this class
    /// never mirrors that into a field of its own.</param>
    public TimelineViewModel(ITimelineSourceFactory factory, Action<Action> postToUi, int maxNotes = DefaultMaxNotes, IProfileStore? profileStore = null, Func<Note, NoteRowActions?>? actionsFactory = null, IKeyStore? keyStore = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNotes, 1);
        _factory = factory;
        _postToUi = postToUi;
        _maxNotes = maxNotes;
        _profileStore = profileStore;
        _actionsFactory = actionsFactory;
        _keyStore = keyStore;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Newest first. Mutated only via <c>postToUi</c>.</summary>
    public ObservableCollection<NoteRow> Notes { get; } = [];

    public string Query
    {
        get => _query;
        set => SetField(ref _query, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetField(ref _isLoading, value);
    }

    /// <summary>The <see cref="Timeline"/> handed to the factory for the current subscription, or null before the first one.</summary>
    public Timeline? ActiveTimeline { get; private set; }

    /// <summary>True when the column's current <see cref="ITimelineSource"/> actually consumes the signed-in
    /// account's identity (<see cref="ITimelineSource.IsAccountBound"/>) -- not merely whether one was resolved
    /// and the subscription went through <see cref="ITimelineSourceFactory.Create"/> rather than
    /// <see cref="ITimelineSourceFactory.CreateAnonymous"/>. A query like <c>from kind("1")</c> opened while
    /// signed in still opens through <c>Create</c> but answers false here, because the compiled query never
    /// touches the account (plan §18.5 -- fixes the L5j wart where this used to be <c>account is not null</c>,
    /// which made <em>every</em> column look account-bound while any key was unlocked). Read by
    /// <see cref="WorkspaceViewModel.NotifyAccountLocked"/> to decide which columns need to fall back to
    /// <see cref="ITimelineSourceFactory.CreateAnonymous"/> once the key store locks or signs out (plan §18.2
    /// item 4, symmetric half).</summary>
    public bool IsAccountBound { get; private set; }

    /// <summary>True when the most recent <see cref="Subscribe"/> failed because its query needed a signed-in
    /// account (<see cref="AccountRequiredException"/>) and none was active. Read by
    /// <see cref="WorkspaceViewModel.NotifyAccountUnlocked"/> to retry exactly these columns once one becomes
    /// active, instead of "press Subscribe again" -- a requirement a user has no way to see (plan §18.2 item 4).
    /// Cleared by the next <see cref="Subscribe"/> attempt, whatever its outcome.</summary>
    public bool NeedsAccountRetry { get; private set; }

    /// <summary>Set by whoever owns this column (its <see cref="TabViewModel"/>, right after
    /// <see cref="TabViewModel.AddColumn"/> builds it) so the column's own close button -- now part of
    /// <see cref="NightlyDawn.App.Views.TimelineView"/>'s header row (plan §11.6 / PR #30 review) --
    /// can ask to be removed without the column needing a reference back to its tab. Null until the owner
    /// sets it.</summary>
    public Action? RequestClose { get; set; }

    /// <summary>Completes when the current stream's consumer has exited (tests await this; the UI does not need it).</summary>
    internal Task StreamCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Starts (or restarts) the column on the current <see cref="Query"/>. Must be called on the UI thread.</summary>
    public void Subscribe()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var kql = Query.Trim();
        if (kql.Length == 0)
        {
            Status = "Enter a query first.";
            return;
        }

        CancelCurrentStream();
        Notes.Clear();
        _knownIds.Clear();

        var generation = ++_generation;
        var timeline = new Timeline(Id: Guid.NewGuid().ToString("N"), Title: kql, KqlQuery: kql);
        ActiveTimeline = timeline;

        StreamCompletion = StartAsync(timeline, generation);
    }

    /// <summary>Resolves the account, opens the source, and runs the stream -- everything <see cref="Subscribe"/>
    /// used to do inline before the account lookup made it asynchronous. Every mutation below goes through
    /// <see cref="Post"/> rather than touching fields directly, because by the time an await here resumes, a
    /// newer <see cref="Subscribe"/> call may already have bumped <see cref="_generation"/> and moved on; the
    /// generation guard inside <see cref="Post"/> is what drops this call's late result instead of clobbering
    /// the current one (the one real race the account lookup introduces).</summary>
    private async Task StartAsync(Timeline timeline, int generation)
    {
        Account? account;
        try
        {
            account = await ResolveAccountAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Post(generation, () => ReportFailure(ex));
            return;
        }

        if (generation != _generation || _disposed)
        {
            return; // Superseded while the account was resolving; nothing was opened yet, so there is nothing to cancel.
        }

        ITimelineSource source;
        try
        {
            source = account is null ? _factory.CreateAnonymous(timeline) : _factory.Create(timeline, account);
        }
        catch (Exception ex)
        {
            Post(generation, () => ReportFailure(ex));
            return;
        }

        var cts = new CancellationTokenSource();
        var token = cts.Token; // Captured here; the background task must never touch the source itself (it may be disposed by then).

        Post(generation, () =>
        {
            _streamCts = cts;
            IsAccountBound = source.IsAccountBound;
            NeedsAccountRetry = false;
            IsLoading = true;
            Status = "Loading…";
        });

        await Task.Run(() => ConsumeAsync(source, generation, token), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Resolves the active account through <see cref="_keyStore"/>, or null when there is no key store
    /// or nothing is unlocked this session -- both are the ordinary "read anonymously" case, not a failure
    /// (<see cref="ITimelineSourceFactory.CreateAnonymous"/> is still a valid, read-only path).</summary>
    private async Task<Account?> ResolveAccountAsync()
    {
        if (_keyStore is null)
        {
            return null;
        }

        try
        {
            var signer = await _keyStore.GetActiveSignerAsync().ConfigureAwait(false);
            return new Account(signer.Pubkey, signer);
        }
        catch (SignerUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Sets the failure state <see cref="StartAsync"/>'s two catch blocks share. <see cref="NightlyDawnException"/>
    /// subtypes write their own user-facing <see cref="Exception.Message"/> (plan §18.2 item 3) -- in particular
    /// <see cref="AccountRequiredException"/>'s, which is the one case that also arms <see cref="NeedsAccountRetry"/>.
    /// Anything else names the exception type only, same as before this leaf: an unexpected exception's message
    /// could echo relay- or input-supplied text, and the status line is not a log.</summary>
    private void ReportFailure(Exception ex)
    {
        IsLoading = false;
        IsAccountBound = false;
        NeedsAccountRetry = ex is AccountRequiredException;
        Status = DescribeFailure(ex);
    }

    private static string DescribeFailure(Exception ex) =>
        ex is NightlyDawnException nde
            ? $"Could not open this timeline: {nde.Message}"
            : $"Could not open this timeline ({ex.GetType().Name}).";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCurrentStream();
    }

    /// <summary>
    /// <see cref="IPublishedNoteSink.NotePublished"/>: inserts the just-published note optimistically (plan
    /// §10.1-4), before any relay echoes it back through the subscription, and reports how many relays
    /// accepted it. Marshals to the UI thread itself (like <see cref="ReportStatus"/>) rather than requiring
    /// the caller to already be on it: <see cref="NoteRowActions"/> awaits <see cref="INotePublisher"/> with
    /// <c>ConfigureAwait(false)</c>, so its continuation -- and this call -- can land on a thread-pool thread,
    /// not necessarily the one that dispatched the click.
    /// </summary>
    public void NotePublished(Note note, PublishResult result) =>
        _postToUi(() =>
        {
            AddNote(note);
            var accepted = result.Outcomes.Count(o => o.Accepted);
            Status = $"Posted · {accepted}/{result.Outcomes.Count} relays accepted.";
        });

    /// <summary>Lets a row action (Repost/React) report a result without touching <see cref="Notes"/> (plan
    /// §10.1-1: results live in <see cref="Status"/>, not on the row). Marshals to the UI thread itself, so
    /// callers -- which run on whatever thread an <c>async void</c> click handler's continuation lands on --
    /// do not need their own dispatcher plumbing.</summary>
    public void ReportStatus(string message) => _postToUi(() => Status = message);

    /// <summary>Cap on a live (post-initial-load) profile lookup: a silent relay must not stall the note
    /// stream waiting for a kind:0 that may never arrive.</summary>
    private static readonly TimeSpan LivePrefetchTimeout = TimeSpan.FromSeconds(3);

    private async Task ConsumeAsync(ITimelineSource source, int generation, CancellationToken cancellationToken)
    {
        // Held until InitialLoadComplete so the whole initial load resolves in one batched PrefetchAsync
        // instead of one REQ per arriving note (plan §8.1-2: "a 200-author column must not issue 200 of
        // those"). Live notes after that point still prefetch one at a time -- lower frequency, and the
        // per-pubkey result is cached already.
        var initialBuffer = new List<Note>();
        var initialLoadDone = false;

        // A stream that ends or faults before ever emitting InitialLoadComplete must still show what it had
        // buffered -- a crash mid-load is not licence to hide notes that already arrived (the pre-batching
        // behaviour showed them immediately; it must still show them, just not one prefetch REQ apiece).
        async Task FlushRemainingBufferAsync()
        {
            if (initialLoadDone || initialBuffer.Count == 0)
            {
                return;
            }

            initialLoadDone = true;
            await FlushInitialLoadAsync(initialBuffer, generation, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await foreach (var update in source.StreamAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!initialLoadDone && update is NoteArrived buffered)
                {
                    BufferCapped(initialBuffer, buffered.Note, _maxNotes);
                    continue; // inserted once the batched prefetch below has run
                }

                if (!initialLoadDone && update is InitialLoadComplete)
                {
                    initialLoadDone = true;
                    await FlushInitialLoadAsync(initialBuffer, generation, cancellationToken).ConfigureAwait(false);
                }
                else if (initialLoadDone && update is NoteArrived live)
                {
                    // Prefetched and awaited here, on the background task, before the row is ever queued for
                    // insertion -- not after (plan §8.1: NoteRow is immutable, so a row built before its
                    // author's profile resolves would show the fallback label forever).
                    await PrefetchAuthorAsync(live.Note.AuthorPubkey, cancellationToken).ConfigureAwait(false);
                }

                Post(generation, () => Apply(update));
            }

            await FlushRemainingBufferAsync().ConfigureAwait(false);

            Post(generation, () =>
            {
                IsLoading = false;
                Status = $"{Notes.Count} notes · stream ended.";
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer subscription or disposed: the generation guard already drops anything late.
        }
        catch (Exception ex)
        {
            await FlushRemainingBufferAsync().ConfigureAwait(false);

            // Exception type only. Messages can carry relay-supplied text, and the status line is not a log.
            Post(generation, () =>
            {
                IsLoading = false;
                Status = $"Timeline stopped: {ex.GetType().Name}.";
            });
        }
    }

    /// <summary>Resolves every distinct author in the buffered initial load with one batched
    /// <see cref="IProfileStore.PrefetchAsync"/> call, then queues each buffered note for insertion --
    /// in that order, so the rows land on the UI thread before <see cref="InitialLoadComplete"/>'s own
    /// <c>IsLoading = false</c> does (a render taken between the two would see an empty column).</summary>
    private async Task FlushInitialLoadAsync(List<Note> buffered, int generation, CancellationToken cancellationToken)
    {
        if (buffered.Count == 0)
        {
            return;
        }

        if (_profileStore is not null)
        {
            var authors = buffered.Select(n => n.AuthorPubkey).Distinct(StringComparer.Ordinal).ToArray();
            try
            {
                await _profileStore.PrefetchAsync(authors, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Unresolved: TryGet below returns null either way, same as "no kind:0 exists".
            }
        }

        foreach (var note in buffered)
        {
            Post(generation, () => AddNote(note));
        }
    }

    /// <summary>Resolves (or confirms absent) one author's profile before its row is built. Swallows every
    /// failure except cancellation: a relay hiccup on the profile lookup must not stop the note stream --
    /// <see cref="NoteRow.From"/> falls back to the pubkey-prefix label when nothing is cached.</summary>
    private async Task PrefetchAuthorAsync(string authorPubkey, CancellationToken cancellationToken)
    {
        if (_profileStore is null)
        {
            return;
        }

        // Linked, not the bare caller token: a relay that never answers must not stall the live stream, but
        // an actual cancellation (resubscribe/dispose) must still propagate immediately, not wait out the cap.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(LivePrefetchTimeout);

        try
        {
            await _profileStore.PrefetchAsync([authorPubkey], timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller's token fired: a real cancellation, propagate it.
        }
        catch (OperationCanceledException)
        {
            // Only our 3s cap fired: fall through to the hex-prefix label, same as an unresolved profile.
        }
        catch (Exception)
        {
            // Unresolved: TryGet below returns null either way, same as "no kind:0 exists".
        }
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread unless a newer subscription has replaced <paramref name="generation"/> meanwhile.</summary>
    private void Post(int generation, Action action) =>
        _postToUi(() =>
        {
            if (generation == _generation && !_disposed)
            {
                action();
            }
        });

    private void Apply(TimelineUpdate update)
    {
        switch (update)
        {
            case NoteArrived arrived:
                AddNote(arrived.Note);
                break;
            case InitialLoadComplete:
                IsLoading = false;
                Status = LiveStatus();
                break;
            default:
                // Forward compatibility: a TimelineUpdate kind this UI does not know about is ignored, not fatal.
                break;
        }
    }

    private void AddNote(Note note)
    {
        if (!_knownIds.Add(note.Id))
        {
            return; // The same event can arrive from several relays (B7); show it once.
        }

        var row = NoteRow.From(note, _profileStore?.TryGet(note.AuthorPubkey), actions: _actionsFactory?.Invoke(note));
        var index = 0;
        while (index < Notes.Count && Notes[index].CreatedAt >= row.CreatedAt)
        {
            index++;
        }

        Notes.Insert(index, row);

        while (Notes.Count > _maxNotes)
        {
            var dropped = Notes[^1];
            Notes.RemoveAt(Notes.Count - 1);
            _knownIds.Remove(dropped.Id);
        }

        if (!IsLoading)
        {
            Status = LiveStatus();
        }
    }

    /// <summary>Inserts <paramref name="note"/> into <paramref name="buffer"/>, sorted newest-first and capped
    /// at <paramref name="maxNotes"/> exactly like <see cref="AddNote"/> caps <see cref="Notes"/> -- a relay
    /// that never emits <see cref="InitialLoadComplete"/> must not grow this buffer without bound (plan
    /// §8.1-2's cap applies before the flush, not just to the column it produces). A pure, static, parameterized
    /// function so the bound itself is directly unit-testable, not just observable through the final column.</summary>
    internal static void BufferCapped(List<Note> buffer, Note note, int maxNotes)
    {
        var index = 0;
        while (index < buffer.Count && buffer[index].CreatedAt >= note.CreatedAt)
        {
            index++;
        }

        buffer.Insert(index, note);

        if (buffer.Count > maxNotes)
        {
            buffer.RemoveAt(buffer.Count - 1);
        }
    }

    private string LiveStatus() => $"{Notes.Count} notes · live";

    private void CancelCurrentStream()
    {
        var cts = _streamCts;
        _streamCts = null;
        if (cts is null)
        {
            return;
        }

        // Cancel before Dispose, always: the consumer only holds the token, and a token whose source was
        // cancelled first stays safe to query, register on, or link after the source is disposed.
        cts.Cancel();
        cts.Dispose();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
