using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using NightlyDawn.App.Composing;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// One tab's timeline content (one column before plan §21.2's inversion): a raw KQL query, a bounded
/// newest-first list of <see cref="NoteRow"/>s, and the
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

    /// <summary><see cref="FocusedIndex"/>'s value for "no row is focused" -- the same -1 a <c>ListBox</c> uses for
    /// no selection, so the two-way binding in <c>TimelineView.axaml</c> never has to translate.</summary>
    public const int NoFocus = -1;

    public const string NothingFocusedStatus = "No note is focused: press j or ↓ to focus one.";
    public const string NoActionsStatus = "This note has no actions wired.";
    public const string NotATextNoteStatus = "Only a text note can be replied to or reposted.";

    private readonly ITimelineSourceFactory _factory;
    private readonly Action<Action> _postToUi;
    private readonly int _maxNotes;
    private readonly IProfileStore? _profileStore;
    private readonly Func<Note, NoteRowActions?>? _actionsFactory;
    private readonly IKeyStore? _keyStore;
    /// <summary>Every note currently in <see cref="Notes"/>, by id. Doubles as the duplicate guard
    /// <c>AddNote</c> always had (the same event arrives from several relays, B7) and, since L7a, as the lookup
    /// <see cref="FindNote"/> offers the search flip: a <see cref="NoteRow"/> carries only what the row shows
    /// (content truncated at <see cref="NoteRow.MaxContentChars"/>, no tags), so a search over the full
    /// <see cref="Note.Content"/>, or through a compiled KQL predicate, needs the <see cref="Note"/> itself. Kept
    /// in step with <see cref="Notes"/> by the same three places that mutate it (Subscribe's clear, AddNote's
    /// insert, AddNote's cap), so it never holds a note the list does not show.</summary>
    private readonly Dictionary<string, Note> _notesById = new(StringComparer.Ordinal);

    private CancellationTokenSource? _streamCts;
    private int _generation;
    private string _query = string.Empty;
    private string _status = IdleStatus;
    private bool _isLoading;
    private bool _disposed;
    private Timeline? _activeTimeline;
    private int _focusedIndex = NoFocus;

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

    /// <summary>Opaque identity for layout persistence (L9): what <c>Column.SelectedTabId</c> in a saved
    /// <see cref="AppState"/> refers to, and what the next launch hands back so the same tab is selected again.
    /// Fresh per instance; <see cref="WorkspaceViewModel"/> overwrites it while restoring a saved layout, which is
    /// the only writer and the reason the setter is internal. Not the <see cref="Timeline.Id"/> inside
    /// <see cref="ActiveTimeline"/> -- that one changes on every <see cref="Subscribe"/>.</summary>
    public string Id { get; internal set; } = Guid.NewGuid().ToString("N");

    /// <summary>Newest first. Mutated only via <c>postToUi</c>.</summary>
    public ObservableCollection<NoteRow> Notes { get; } = [];

    /// <summary>
    /// The keyboard cursor (L17): the index into <see cref="Notes"/> of the row the timeline keys act on, or
    /// <see cref="NoFocus"/>. This is the fork's <c>FocusedStatus</c> (<c>TimelineViewModelBase.cs:344-358</c>)
    /// as an index, and it is <b>the same state as the list's selection</b>: <c>TimelineView.axaml</c> binds
    /// <c>TimelineList.SelectedIndex</c> to it two-way, so a mouse click on a row and a <c>j</c> keypress both
    /// land here, and the focused row is painted through the ordinary <c>ListBoxItem:selected</c> look rather than
    /// an invisible second cursor.
    ///
    /// <para><b>Deliberate difference from the fork</b> (Lead ruling, plan §68.9.1): StarryEyes keeps focus (one
    /// cursor) and selection (many checked rows, <c>SelectedStatuses</c>) apart because it has multi-select and
    /// bulk actions. This port's list is <c>SelectionMode="Single"</c> and bulk actions are out of scope, so two
    /// notions would only be two authorities that silently disagree the first time someone clicks a row.</para>
    ///
    /// <para>The setter accepts whatever the list pushes and clamps anything outside <see cref="Notes"/> to
    /// <see cref="NoFocus"/>; <see cref="AddNote"/> keeps the cursor on the <em>same note</em> when newer notes
    /// are inserted above it (the fork's cursor is a reference, so it never drifted either), and drops it when
    /// the focused row is capped away or the column is cleared by <see cref="Subscribe"/>.</para>
    /// </summary>
    public int FocusedIndex
    {
        get => _focusedIndex;
        set => SetField(ref _focusedIndex, value >= 0 && value < Notes.Count ? value : NoFocus);
    }

    /// <summary>The row under the cursor, or null when <see cref="FocusedIndex"/> is <see cref="NoFocus"/>.</summary>
    public NoteRow? FocusedNote => _focusedIndex >= 0 && _focusedIndex < Notes.Count ? Notes[_focusedIndex] : null;

    /// <summary>Fork <c>FocusDown</c> (<c>:368-376</c>): no cursor goes to the first row; the last row stays put.</summary>
    public void FocusDown()
    {
        if (Notes.Count == 0)
        {
            return;
        }

        var next = _focusedIndex < 0 ? 0 : _focusedIndex + 1;
        if (next < Notes.Count)
        {
            FocusedIndex = next;
        }
    }

    /// <summary>Fork <c>FocusUp</c> (<c>:361-366</c>): moving up from the first row <em>clears</em> the cursor rather
    /// than staying on it -- kept as-is, since it is what the key table's users have in their fingers.</summary>
    public void FocusUp()
    {
        if (Notes.Count == 0 || _focusedIndex < 0)
        {
            return;
        }

        FocusedIndex = _focusedIndex - 1;
    }

    /// <summary>Fork <c>FocusTop</c> (<c>:378-382</c>; <c>s+k</c> and <c>0</c>).</summary>
    public void FocusTop()
    {
        if (Notes.Count > 0)
        {
            FocusedIndex = 0;
        }
    }

    /// <summary>Fork <c>FocusBottom</c> (<c>:384-388</c>; <c>s+j</c>).</summary>
    public void FocusBottom()
    {
        if (Notes.Count > 0)
        {
            FocusedIndex = Notes.Count - 1;
        }
    }

    /// <summary>Fork <c>ClearSelect</c> on <c>escape</c>. With focus and selection being one state here, this
    /// is also what clears the row highlight.</summary>
    public void ClearFocus() => FocusedIndex = NoFocus;

    /// <summary><c>r</c> -> <see cref="NoteRowActions.BeginReply"/> on the focused row. Every way this can do
    /// nothing says so on <see cref="Status"/> (plan §68.9: a key that is pressed must never be silent): no cursor,
    /// a row built without actions, or a kind that <see cref="NoteRow.CanReplyOrRepost"/> refuses -- the same
    /// guard the row's Reply button is disabled by.</summary>
    public void ReplyToFocused()
    {
        if (TryGetFocusedActions(out var row, out var actions) && RowCanReplyOrRepost(row))
        {
            actions.BeginReply();
        }
    }

    /// <summary><c>c+r</c> -> <see cref="NoteRowActions.RepostAsync"/> on the focused row. Failures (no publisher,
    /// no unlocked key, relay rejections) are reported on <see cref="Status"/> by <see cref="NoteRowActions"/>
    /// itself; this never throws.</summary>
    public Task RepostFocusedAsync() =>
        TryGetFocusedActions(out var row, out var actions) && RowCanReplyOrRepost(row)
            ? actions.RepostAsync()
            : Task.CompletedTask;

    /// <summary><c>f</c> / <c>Oem1</c> -> <see cref="NoteRowActions.ReactAsync"/> (♥, the fork's <c>Favorite</c>) on
    /// the focused row. Same failure reporting as <see cref="RepostFocusedAsync"/>; any kind can be reacted to.</summary>
    public Task ReactToFocusedAsync() =>
        TryGetFocusedActions(out _, out var actions)
            ? actions.ReactAsync()
            : Task.CompletedTask;

    /// <summary>What <c>c+c</c> copies: the focused note's <em>full</em> <see cref="Note.Content"/> from
    /// <see cref="FindNote"/>, not the row's <see cref="NoteRow.DisplayContent"/>, which is truncated at
    /// <see cref="NoteRow.MaxContentChars"/> for layout. Null (with <see cref="Status"/> set) when nothing is
    /// focused. The clipboard write itself is the view's job -- this class has no Avalonia dependency.</summary>
    public string? FocusedContentForCopy()
    {
        if (FocusedNote is not { } row)
        {
            Status = NothingFocusedStatus;
            return null;
        }

        return FindNote(row.Id)?.Content ?? row.DisplayContent;
    }

    private bool TryGetFocusedActions([NotNullWhen(true)] out NoteRow? row, [NotNullWhen(true)] out NoteRowActions? actions)
    {
        row = FocusedNote;
        actions = row?.Actions;
        if (row is null)
        {
            Status = NothingFocusedStatus;
            return false;
        }

        if (actions is null)
        {
            Status = NoActionsStatus;
            return false;
        }

        return true;
    }

    private bool RowCanReplyOrRepost(NoteRow row)
    {
        if (row.CanReplyOrRepost)
        {
            return true;
        }

        Status = NotATextNoteStatus;
        return false;
    }

    /// <summary>The <see cref="Note"/> behind a row of <see cref="Notes"/>, or null when no row with that id is
    /// currently shown (L7a). This is how <see cref="Search.SearchResultViewModel"/> matches keywords against the
    /// full content rather than the row's truncated <see cref="NoteRow.DisplayContent"/>, and how a <c>?</c> query's
    /// compiled predicate gets a <see cref="Note"/> to evaluate. Read on the UI thread, like <see cref="Notes"/>.</summary>
    public Note? FindNote(string id) => _notesById.GetValueOrDefault(id);

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
    public Timeline? ActiveTimeline
    {
        get => _activeTimeline;
        private set => SetField(ref _activeTimeline, value);
    }

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

    /// <summary>Set by whoever owns this tab (its <see cref="ColumnViewModel"/>, right after
    /// <see cref="ColumnViewModel.AddTab"/> builds it) so the tab's own close button -- part of
    /// <see cref="NightlyDawn.App.Views.TimelineView"/>'s header row (plan §11.6 / PR #30 review) --
    /// can ask to be removed without this class needing a reference back to its column. Null until the owner
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
        FocusedIndex = NoFocus; // Before the clear, so the cursor never points past the end of an emptied list.
        Notes.Clear();
        _notesById.Clear();

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
        if (!_notesById.TryAdd(note.Id, note))
        {
            return; // The same event can arrive from several relays (B7); show it once.
        }

        var row = NoteRow.From(note, _profileStore?.TryGet(note.AuthorPubkey), actions: _actionsFactory?.Invoke(note));
        var index = 0;
        while (index < Notes.Count && Notes[index].CreatedAt >= row.CreatedAt)
        {
            index++;
        }

        // Captured before the insert: a bound ListBox shifts its own SelectedIndex on the CollectionChanged the
        // insert raises and pushes that back into FocusedIndex two-way, before control returns here. Deriving the
        // new cursor from the pre-insert value makes this line idempotent with that push (same target index)
        // instead of shifting the cursor twice; without a list bound (unit tests, no view), it is the only shift.
        var focusedBefore = _focusedIndex;
        Notes.Insert(index, row);
        if (focusedBefore >= index)
        {
            FocusedIndex = focusedBefore + 1; // The cursor stays on the note it was on, not on the index (fork: FocusedStatus is a reference).
        }

        while (Notes.Count > _maxNotes)
        {
            var dropped = Notes[^1];
            Notes.RemoveAt(Notes.Count - 1);
            _notesById.Remove(dropped.Id);
        }

        if (_focusedIndex >= Notes.Count)
        {
            FocusedIndex = NoFocus; // The focused row was the one capped away.
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
