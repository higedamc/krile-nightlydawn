using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// One column: a set of tabs (each a <see cref="TimelineViewModel"/>) plus which one is selected, and nothing
/// else (plan §21.2 / L4b) -- the fork (StarryEyes <c>MainArea.xaml:196</c>, <c>ColumnTemplate:83/96/118</c>)
/// nests <c>Tabs</c> inside <c>Columns</c>; this app shipped the opposite (<c>TabViewModel.Columns</c>) until
/// this leaf. The old <c>TabViewModel.Title</c> has no equivalent here on purpose -- StarryEyes columns are not
/// titled, and renaming a column is out of this leaf's scope (plan §21.2's "やらないこと").
///
/// <para>Tabs are built by <paramref name="tabFactory"/> passed in at construction -- the same delegate
/// <see cref="WorkspaceViewModel"/> was given, so every tab in the workspace (not just this column's) comes
/// from the one factory the host built in <c>MainWindow</c> (plan §11.1: "列の生成は1箇所に閉じる", now "タブの
/// 生成は1箇所に閉じる").</para>
/// </summary>
public sealed class ColumnViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<TimelineViewModel> _tabFactory;
    private TimelineViewModel? _selectedTab;
    private double _minWidthHint = 420;
    private bool _disposed;

    /// <param name="id">Identity for layout persistence (L9); a fresh one when null. <see cref="WorkspaceViewModel"/>
    /// passes a saved column's id back in while restoring, so the saved <c>ActiveColumnId</c> still names it.</param>
    public ColumnViewModel(Func<TimelineViewModel> tabFactory, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(tabFactory);
        _tabFactory = tabFactory;
        Id = id ?? Guid.NewGuid().ToString("N");
        Tabs.CollectionChanged += OnTabsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when something a saved layout records about this column changed (L9): a tab was added
    /// or removed, the selected tab changed, or a tab's <see cref="TimelineViewModel.Query"/> changed.
    /// Deliberately <em>not</em> raised for a tab's notes, status or loading state -- those change continuously
    /// while a stream runs and are not part of the layout. <see cref="WorkspaceViewModel.LayoutChanged"/>
    /// aggregates this across columns; <see cref="Persistence.LayoutPersistence"/> is the consumer.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Opaque identity for layout persistence (L9): what a saved <c>AppState.ActiveColumnId</c> names.</summary>
    public string Id { get; }

    /// <summary>What <see cref="WorkspaceView"/>'s per-column template binds this column's layout root's
    /// <c>MinWidth</c> to (plan §26.5 / L4c ④). <see cref="WorkspaceViewModel"/> is the only writer -- it
    /// is the one place that knows how many sibling columns exist, which is what this value needs to react
    /// to (one column gets room to clear <c>TimelineList</c>'s 600 DIP floor; several share 1280 DIP and
    /// must not overflow it). 420 is the pre-this-leaf default so a column never renders narrower than it
    /// used to before <see cref="WorkspaceViewModel"/> runs its first width pass.</summary>
    public double MinWidthHint
    {
        get => _minWidthHint;
        set => SetField(ref _minWidthHint, value);
    }

    /// <summary>This column's tabs, left-to-right (plan §11.5, carried over). Can reach zero after
    /// <see cref="CloseTab"/> removes the last one -- nothing in this class stops that;
    /// <see cref="WorkspaceViewModel.ActiveSink"/> is what turns an empty column into a null compose target.</summary>
    public ObservableCollection<TimelineViewModel> Tabs { get; } = [];

    /// <summary>Whether <see cref="AddTab"/> may be called: false once <see cref="Tabs"/> holds
    /// <see cref="AppState.MaxTabsPerColumn"/> (L9-2, plan §34). The one producer-side owner of that bound, the
    /// same way <see cref="WorkspaceViewModel.CanAddColumn"/> owns the column count: <c>TabStripView</c>'s
    /// <c>AddTabButton</c> binds <c>IsEnabled</c> to it and <see cref="AddTab"/> refuses when it is false. Raised
    /// through <see cref="PropertyChanged"/> whenever <see cref="Tabs"/> changes.</summary>
    public bool CanAddTab => Tabs.Count < AppState.MaxTabsPerColumn;

    /// <summary>Null when <see cref="Tabs"/> is empty.</summary>
    public TimelineViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetField(ref _selectedTab, value))
            {
                LayoutChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Builds a new tab through the factory, appends it, and selects it. Throws
    /// <see cref="InvalidOperationException"/> when <see cref="CanAddTab"/> is false -- see
    /// <see cref="WorkspaceViewModel.AddColumn"/> for why that is a throw and not a no-op.</summary>
    public TimelineViewModel AddTab()
    {
        if (!CanAddTab)
        {
            throw new InvalidOperationException($"This column already holds {AppState.MaxTabsPerColumn} tabs, the most a saved layout can carry (AppState.MaxTabsPerColumn); check CanAddTab first.");
        }

        var tab = _tabFactory();
        tab.RequestClose = () => CloseTab(tab); // closes over this tab only, same as CreateTab's actionsFactory.
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>Removes <paramref name="tab"/> from this column and disposes it (plan §11.3: closing a tab
    /// must stop its relay subscription, not just drop the UI's reference to it). A no-op if the tab is not
    /// (or no longer) in this column.</summary>
    public void CloseTab(TimelineViewModel tab)
    {
        if (!Tabs.Remove(tab))
        {
            return;
        }

        tab.Dispose();

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs.FirstOrDefault();
        }
    }

    /// <summary>Disposes every tab (plan §11.3: closing this column closes everything under it).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var tab in Tabs)
        {
            tab.Dispose();
        }
    }

    /// <summary>Keeps <see cref="LayoutChanged"/> fed from the tabs themselves: every tab that enters
    /// <see cref="Tabs"/> (through <see cref="AddTab"/> or a restore) is watched for its <c>Query</c>, every tab that
    /// leaves is unwatched. Hooked on the collection rather than inside <see cref="AddTab"/> so there is exactly one
    /// place a tab can enter or leave this column without the layout noticing -- none.</summary>
    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var tab in e.OldItems.OfType<TimelineViewModel>())
            {
                tab.PropertyChanged -= OnTabPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var tab in e.NewItems.OfType<TimelineViewModel>())
            {
                tab.PropertyChanged += OnTabPropertyChanged;
            }
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAddTab)));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.Query))
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
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
