using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;

namespace NightlyDawn.App.Search;

/// <summary>
/// One committed search's results (plan §37.2 item 6/7: a new instance per commit, the previous one disposed): the
/// rows of one <see cref="TimelineViewModel"/> that a predicate accepts, kept live while that tab's stream keeps
/// delivering. The fork's <c>SearchResultModel</c> was a timeline of its own subscribed to the status broadcaster;
/// here the tab already is that timeline, so this is a filtered projection of <see cref="TimelineViewModel.Notes"/>
/// instead -- which is also what "AND the current tab's filter" (plan §37.2 item 10, <c>SearchResultModel.cs:89-96</c>)
/// means in this app: a note is in the tab's list only because the tab's own query accepted it.
///
/// <para>Rows are the tab's own <see cref="NoteRow"/> instances, not copies, so <c>NoteRowView</c> renders them with
/// the same avatar, label and row actions, and Reply/Repost from a result still reports to the tab it came from
/// (plan §11.1's closure holds). The predicate sees the <see cref="Note"/>, found through
/// <see cref="TimelineViewModel.FindNote"/>, because the row's <see cref="NoteRow.DisplayContent"/> is truncated and
/// tagless. UI thread only, like the tab's own collection.</para>
/// </summary>
public sealed class SearchResultViewModel : IDisposable
{
    private readonly TimelineViewModel _tab;
    private readonly Func<Note, bool> _predicate;
    private bool _disposed;

    public SearchResultViewModel(TimelineViewModel tab, Func<Note, bool> predicate, string query)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(query);
        _tab = tab;
        _predicate = predicate;
        Query = query;
        Rebuild();
        _tab.Notes.CollectionChanged += OnTabNotesChanged;
    }

    /// <summary>The text this result was committed from, exactly as typed (the <c>?</c> included in query mode). What
    /// L7b's pin-to-tab turns into a tab title.</summary>
    public string Query { get; }

    /// <summary>Newest first, same order as the tab. Mutated only on the UI thread, from the tab's own notifications.</summary>
    public ObservableCollection<NoteRow> Notes { get; } = [];

    private void OnTabNotesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (var row in e.NewItems.OfType<NoteRow>())
                {
                    if (Accepts(row))
                    {
                        InsertSorted(row);
                    }
                }

                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (var row in e.OldItems.OfType<NoteRow>())
                {
                    Notes.Remove(row);
                }

                break;

            default:
                // Replace/Move/Reset: the tab's Subscribe() clears everything (Reset); rebuilding from the list is
                // the only answer that is right for every one of these, and the list is at most DefaultMaxNotes long.
                Rebuild();
                break;
        }
    }

    private void Rebuild()
    {
        Notes.Clear();
        foreach (var row in _tab.Notes)
        {
            if (Accepts(row))
            {
                Notes.Add(row);
            }
        }
    }

    /// <summary>A row whose <see cref="Note"/> the tab no longer knows (it cannot happen while the two collections
    /// are kept in step, but the lookup is nullable) is not a match: the predicate has nothing to evaluate.</summary>
    private bool Accepts(NoteRow row) => _tab.FindNote(row.Id) is { } note && _predicate(note);

    private void InsertSorted(NoteRow row)
    {
        var index = 0;
        while (index < Notes.Count && Notes[index].CreatedAt >= row.CreatedAt)
        {
            index++;
        }

        Notes.Insert(index, row);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tab.Notes.CollectionChanged -= OnTabNotesChanged;
    }
}
