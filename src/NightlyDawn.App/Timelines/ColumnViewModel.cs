using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

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

    public ColumnViewModel(Func<TimelineViewModel> tabFactory)
    {
        ArgumentNullException.ThrowIfNull(tabFactory);
        _tabFactory = tabFactory;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>Null when <see cref="Tabs"/> is empty.</summary>
    public TimelineViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    /// <summary>Builds a new tab through the factory, appends it, and selects it.</summary>
    public TimelineViewModel AddTab()
    {
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
