using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

    /// <summary>Plan §26.5 / L4c ④: the single default column must clear <c>TimelineList</c>'s pre-existing
    /// 600 DIP floor (<c>MainWindowRenderTests</c>) now that the column head no longer has a <c>QueryBox</c>
    /// forcing it wider on its own. 640 clears 600 after the column's Border padding (24 DIP) is subtracted.</summary>
    private const double SoleColumnMinWidth = 640;

    /// <summary>Two or more columns instead share the window (plan §26.5's "3 列が 1280 幅に収まる"): 360 is the
    /// floor this leaf inherited from before ④, chosen then only as "wide enough for a tab strip + timeline",
    /// not against any window-width budget -- <c>WindowChromeRenderTests</c> measures the real total.</summary>
    private const double SharedColumnMinWidth = 360;

    public WorkspaceViewModel(Func<TimelineViewModel> tabFactory)
    {
        ArgumentNullException.ThrowIfNull(tabFactory);
        _tabFactory = tabFactory;
        Columns.CollectionChanged += OnColumnsChanged;

        var column = NewColumn();
        Columns.Add(column);
        SelectedColumn = column;
        RecalculateColumnWidths();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when anything <see cref="CaptureLayout"/> would record changed (L9): a column added or
    /// removed, <see cref="SelectedColumn"/>, or any column's <see cref="ColumnViewModel.LayoutChanged"/> (tabs,
    /// selected tab, a tab's query). Not raised for notes, status or loading state. Also raised during
    /// <see cref="RestoreLayout"/> -- the consumer (<see cref="Persistence.LayoutPersistence"/>) only subscribes
    /// after the restore is complete, which is how "restoring never saves" is kept true.</summary>
    public event EventHandler? LayoutChanged;

    public ObservableCollection<ColumnViewModel> Columns { get; } = [];

    /// <summary>Whether <see cref="AddColumn"/> may be called: false once <see cref="Columns"/> holds
    /// <see cref="AppState.MaxColumns"/> (L9-2, plan §34). This property is the one owner of that bound on the
    /// producing side -- <c>WorkspaceView</c>'s <c>AddColumnButton</c> binds <c>IsEnabled</c> to it and
    /// <see cref="AddColumn"/> refuses when it is false, so there is no state a user can reach from which the
    /// store's <c>SaveAsync</c> would refuse the layout. Before this property existed the store was the only gate,
    /// and a 33rd column made every later save fail silently for the rest of the session: the user's work from
    /// that point on was gone at the next launch (Lead's probe B). Raised through <see cref="PropertyChanged"/>
    /// whenever <see cref="Columns"/> changes, so the bound button follows it.</summary>
    public bool CanAddColumn => Columns.Count < AppState.MaxColumns;

    public ColumnViewModel? SelectedColumn
    {
        get => _selectedColumn;
        set
        {
            if (SetField(ref _selectedColumn, value))
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Where <see cref="ComposeViewModel"/> sends a successful post (plan §11.2): the selected
    /// column's selected tab, else that column's first tab, else null if the column has no tabs. Not cached --
    /// read fresh on every <see cref="ComposeViewModel.PostAsync"/> call through the
    /// <c>Func&lt;IPublishedNoteSink?&gt;</c> it was built with, so switching columns or tabs takes effect on
    /// the very next post with no extra event wiring between this class and that one.
    /// </summary>
    public IPublishedNoteSink? ActiveSink => SelectedColumn?.SelectedTab ?? SelectedColumn?.Tabs.FirstOrDefault();

    /// <summary>Adds a new column (with one tab, same as the default) and selects it. Plan §11.5: no
    /// name-edit UI. Throws <see cref="InvalidOperationException"/> when <see cref="CanAddColumn"/> is false:
    /// the button that calls this is disabled in that state, so reaching the throw means a caller skipped the
    /// precondition, which is a programming error and not something to swallow (plan §34: a button that can be
    /// pressed and does nothing is the shape L4c ④ rejected; a disabled one is honest).</summary>
    public ColumnViewModel AddColumn()
    {
        if (!CanAddColumn)
        {
            throw new InvalidOperationException($"The workspace already holds {AppState.MaxColumns} columns, the most a saved layout can carry (AppState.MaxColumns); check CanAddColumn first.");
        }

        var column = NewColumn();
        Columns.Add(column);
        SelectedColumn = column;
        RecalculateColumnWidths();
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

        RecalculateColumnWidths();
    }

    /// <summary>Replaces the built-in layout with a saved one (L9, plan §32.2): one <see cref="ColumnViewModel"/>
    /// per <see cref="Column"/> carrying that column's id, one tab per <see cref="Timeline"/> carrying its id and
    /// <see cref="Timeline.KqlQuery"/>, the saved selections re-applied, and every tab with a non-blank query
    /// subscribed -- a client that brought the columns back empty and waited for the user to press Subscribe in
    /// each one would have restored the furniture but not the room. Must be called on the UI thread, before the
    /// user has touched anything, with a state the store has already bounded (the store is the one gate;
    /// this method trusts its caller the way <see cref="AddColumn"/> does).
    ///
    /// <para>A no-op for an empty <paramref name="columns"/>: that is <see cref="AppState.Default"/>'s meaning
    /// ("no saved layout"), so the built-in layout the constructor made stays, unsaved
    /// ([[persisted-default-erases-unset]]). Selection ids that name nothing fall back to the first column /
    /// first tab rather than throwing: a stale id is a reason to pick something, not a reason not to start.</para>
    /// </summary>
    public void RestoreLayout(IReadOnlyList<Column> columns, string? activeColumnId)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (columns.Count == 0)
        {
            return;
        }

        foreach (var existing in Columns.ToArray())
        {
            Columns.Remove(existing);
            existing.Dispose();
        }

        foreach (var saved in columns)
        {
            var column = new ColumnViewModel(_tabFactory, saved.Id);
            foreach (var savedTab in saved.Tabs)
            {
                var tab = column.AddTab();
                tab.Id = savedTab.Id;
                tab.Query = savedTab.KqlQuery;
            }

            column.SelectedTab = column.Tabs.FirstOrDefault(t => t.Id == saved.SelectedTabId) ?? column.Tabs.FirstOrDefault();
            Columns.Add(column);
        }

        SelectedColumn = Columns.FirstOrDefault(c => c.Id == activeColumnId) ?? Columns[0];
        RecalculateColumnWidths();

        foreach (var tab in Columns.SelectMany(c => c.Tabs))
        {
            if (!string.IsNullOrWhiteSpace(tab.Query))
            {
                tab.Subscribe();
            }
        }
    }

    /// <summary>The current layout as the <see cref="AppState"/> a store writes (L9): columns left to right,
    /// each tab as a <see cref="Timeline"/> whose <see cref="Timeline.Title"/> and <see cref="Timeline.KqlQuery"/>
    /// are both the tab's live <see cref="TimelineViewModel.Query"/> (the same thing <see cref="TimelineViewModel.Subscribe"/>
    /// puts in its own <see cref="Timeline"/>; there is no separate user-set title yet, plan §21.2), the
    /// selections by id, and <paramref name="settings"/> as given -- this class knows nothing about the theme,
    /// the caller supplies it. Nothing else: no notes, no status, no key material, nothing from
    /// <see cref="IKeyStore"/>; the four fields of the record are the whole surface.</summary>
    public AppState CaptureLayout(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var columns = Columns
            .Select(c => new Column(
                c.Id,
                c.Tabs.Select(t => new Timeline(t.Id, Title: t.Query, KqlQuery: t.Query)).ToArray(),
                c.SelectedTab?.Id))
            .ToArray();
        return new AppState(AppState.CurrentSchemaVersion, columns, SelectedColumn?.Id, settings);
    }

    private ColumnViewModel NewColumn()
    {
        var column = new ColumnViewModel(_tabFactory);
        column.AddTab();
        return column;
    }

    /// <summary>Mirror of <see cref="ColumnViewModel"/>'s tab watching, one level up: every column that enters
    /// <see cref="Columns"/> feeds <see cref="LayoutChanged"/>, every column that leaves stops.</summary>
    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var column in e.OldItems.OfType<ColumnViewModel>())
            {
                column.LayoutChanged -= OnColumnLayoutChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var column in e.NewItems.OfType<ColumnViewModel>())
            {
                column.LayoutChanged += OnColumnLayoutChanged;
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddColumn)));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnLayoutChanged(object? sender, EventArgs e) => LayoutChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Pushes <see cref="SoleColumnMinWidth"/> or <see cref="SharedColumnMinWidth"/> onto every
    /// column's <see cref="ColumnViewModel.MinWidthHint"/>, called after every <see cref="Columns"/> mutation
    /// (plan §26.5 / L4c ④). Re-stamps every column rather than only the one that changed: closing a column
    /// down to one left must widen that survivor back out, which touching only the added/removed column
    /// would miss.</summary>
    private void RecalculateColumnWidths()
    {
        var width = Columns.Count <= 1 ? SoleColumnMinWidth : SharedColumnMinWidth;
        foreach (var column in Columns)
        {
            column.MinWidthHint = width;
        }
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

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
