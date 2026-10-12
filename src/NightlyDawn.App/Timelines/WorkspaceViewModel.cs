using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NightlyDawn.App.Composing;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// The whole workspace: a set of <see cref="ColumnViewModel"/>s plus which one is selected (plan §21.2 / L4b --
/// inverts the pre-L4b shape, where this class held <c>Tabs</c> and <c>TabViewModel</c> held <c>Columns</c>; the
/// fork puts columns on the outside). Starts with one column holding one tab, so
/// <c>QueryBox</c>/<c>SubscribeButton</c>/<c>TimelineList</c> name resolution stays unique by default (plan
/// §11.4) -- the existing render tests never see more than one tab unless a test explicitly adds more.
///
/// <para><paramref name="tabFactory"/> is built once by the host (<c>MainWindow</c>) and handed to every
/// column this workspace ever creates (plan §11.1): each tab it builds closes over that same tab instance for
/// its row actions' sink, so a row's Repost/React always reports back to the tab it came from, not whichever
/// tab happened to exist first.</para>
/// </summary>
public sealed class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<TimelineViewModel> _tabFactory;
    private ColumnViewModel? _selectedColumn;
    private bool _disposed;

    public WorkspaceViewModel(Func<TimelineViewModel> tabFactory)
    {
        ArgumentNullException.ThrowIfNull(tabFactory);
        _tabFactory = tabFactory;

        var column = NewColumn();
        Columns.Add(column);
        SelectedColumn = column;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ColumnViewModel> Columns { get; } = [];

    public ColumnViewModel? SelectedColumn
    {
        get => _selectedColumn;
        set => SetField(ref _selectedColumn, value);
    }

    /// <summary>Where <see cref="ComposeViewModel"/> sends a successful post (plan §11.2): the selected
    /// column's selected tab, else that column's first tab, else null if the column has no tabs. Not cached --
    /// read fresh on every <see cref="ComposeViewModel.PostAsync"/> call through the
    /// <c>Func&lt;IPublishedNoteSink?&gt;</c> it was built with, so switching columns or tabs takes effect on
    /// the very next post with no extra event wiring between this class and that one.
    /// </summary>
    public IPublishedNoteSink? ActiveSink => SelectedColumn?.SelectedTab ?? SelectedColumn?.Tabs.FirstOrDefault();

    /// <summary>Adds a new column (with one tab, same as the default) and selects it. Plan §11.5: no
    /// name-edit UI, no persistence -- process lifetime only.</summary>
    public ColumnViewModel AddColumn()
    {
        var column = NewColumn();
        Columns.Add(column);
        SelectedColumn = column;
        return column;
    }

    /// <summary>Closes <paramref name="column"/>, disposing every tab under it (plan §11.3). Refuses to close
    /// the last column (plan §11.5: there is no empty-workspace recovery UI to build for v1) and refuses a
    /// column this workspace does not have -- both are no-ops, not exceptions, since a stale button click has
    /// nothing useful to report back for either case.</summary>
    public void CloseColumn(ColumnViewModel column)
    {
        if (Columns.Count <= 1 || !Columns.Remove(column))
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedColumn, column);
        column.Dispose();

        if (wasSelected)
        {
            SelectedColumn = Columns[^1];
        }
    }

    private ColumnViewModel NewColumn()
    {
        var column = new ColumnViewModel(_tabFactory);
        column.AddTab();
        return column;
    }

    /// <summary>Retries exactly the tabs, across every column, whose last <see cref="TimelineViewModel.Subscribe"/>
    /// failed because it needed a signed-in account and none was active yet
    /// (<see cref="TimelineViewModel.NeedsAccountRetry"/>). Called once the key store reports an active
    /// signer (plan §18.2 item 4): "press Subscribe again" is not a requirement a user can see, so this does it
    /// for them. A tab whose query never needed an account is untouched -- re-subscribing it would just
    /// restart an already-working stream for no reason.</summary>
    public void NotifyAccountUnlocked()
    {
        foreach (var column in Columns)
        {
            foreach (var tab in column.Tabs)
            {
                if (tab.NeedsAccountRetry)
                {
                    tab.Subscribe();
                }
            }
        }
    }

    /// <summary>Symmetric counterpart of <see cref="NotifyAccountUnlocked"/>: when the key store locks or signs
    /// out, every tab currently bound to that account (<see cref="TimelineViewModel.IsAccountBound"/>)
    /// is re-subscribed so it falls back to <see cref="ITimelineSourceFactory.CreateAnonymous"/> instead of
    /// silently continuing to show a feed for an account that is no longer unlocked.</summary>
    public void NotifyAccountLocked()
    {
        foreach (var column in Columns)
        {
            foreach (var tab in column.Tabs)
            {
                if (tab.IsAccountBound)
                {
                    tab.Subscribe();
                }
            }
        }
    }

    /// <summary>Disposes every column (plan §11.3: closing the window closes everything under the workspace).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var column in Columns)
        {
            column.Dispose();
        }
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
