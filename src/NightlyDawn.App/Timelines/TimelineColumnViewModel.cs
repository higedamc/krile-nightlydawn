using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// One timeline column: a raw KQL query, a bounded newest-first list of <see cref="NoteRow"/>s, and the
/// loading state. Subscribing asks the <see cref="ITimelineSourceFactory"/> for an anonymous (read-only,
/// no signer) source and consumes its <see cref="ITimelineSource.StreamAsync"/> on a background task;
/// account-bound columns arrive with the key store (1b/1f). Every state change is marshalled
/// through <c>postToUi</c>, so the class has no Avalonia dependency and is unit-tested with a synchronous
/// poster. "Loading" ends on <see cref="InitialLoadComplete"/> (B6/B9), not on the first note.
/// </summary>
public sealed class TimelineColumnViewModel : INotifyPropertyChanged, IDisposable
{
    public const int DefaultMaxNotes = 500;
    public const string IdleStatus = "Enter a query and press Subscribe.";

    private readonly ITimelineSourceFactory _factory;
    private readonly Action<Action> _postToUi;
    private readonly int _maxNotes;
    private readonly HashSet<string> _knownIds = new(StringComparer.Ordinal);

    private CancellationTokenSource? _streamCts;
    private int _generation;
    private string _query = string.Empty;
    private string _status = IdleStatus;
    private bool _isLoading;
    private bool _disposed;

    public TimelineColumnViewModel(ITimelineSourceFactory factory, Action<Action> postToUi, int maxNotes = DefaultMaxNotes)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(postToUi);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNotes, 1);
        _factory = factory;
        _postToUi = postToUi;
        _maxNotes = maxNotes;
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

        ITimelineSource source;
        try
        {
            source = _factory.CreateAnonymous(timeline);
        }
        catch (Exception ex)
        {
            IsLoading = false;
            Status = $"Could not open this timeline ({ex.GetType().Name}).";
            return;
        }

        IsLoading = true;
        Status = "Loading…";

        var cts = new CancellationTokenSource();
        var token = cts.Token; // Read here, on the UI thread: the background task must never touch the source itself (it may be disposed by then).
        _streamCts = cts;
        StreamCompletion = Task.Run(() => ConsumeAsync(source, generation, token), CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelCurrentStream();
    }

    private async Task ConsumeAsync(ITimelineSource source, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in source.StreamAsync(cancellationToken).ConfigureAwait(false))
            {
                Post(generation, () => Apply(update));
            }

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
            // Exception type only. Messages can carry relay-supplied text, and the status line is not a log.
            Post(generation, () =>
            {
                IsLoading = false;
                Status = $"Timeline stopped: {ex.GetType().Name}.";
            });
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

        var row = NoteRow.From(note);
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
