using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NightlyDawn.App.Composing;
using NightlyDawn.Core;

namespace NightlyDawn.App.Timelines;

/// <summary>
/// The whole workspace: a set of <see cref="TabViewModel"/>s plus which one is selected (plan §11.1). Starts
/// with one tab ("Home") holding one column, so <c>QueryBox</c>/<c>SubscribeButton</c>/<c>TimelineList</c>
/// name resolution stays unique by default (plan §11.4) -- the existing render tests never see more than one
/// column unless a test explicitly adds more.
///
/// <para><paramref name="columnFactory"/> is built once by the host (<c>MainWindow</c>) and handed to every
/// tab this workspace ever creates (plan §11.1): each column it builds closes over that same column instance
/// for its row actions' sink, so a row's Repost/React always reports back to the column it came from, not
/// whichever column happened to exist first.</para>
/// </summary>
public sealed class WorkspaceViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<TimelineViewModel> _columnFactory;
    private TabViewModel? _selectedTab;
    private bool _disposed;

    public WorkspaceViewModel(Func<TimelineViewModel> columnFactory)
    {
        ArgumentNullException.ThrowIfNull(columnFactory);
        _columnFactory = columnFactory;

        var home = NewTab("Home");
        Tabs.Add(home);
        SelectedTab = home;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set => SetField(ref _selectedTab, value);
    }

    /// <summary>
    /// Where <see cref="ComposeViewModel"/> sends a successful post (plan §11.2): the selected tab's selected
    /// column, else that tab's first column, else null if the tab has no columns. Not cached -- read fresh on
    /// every <see cref="ComposeViewModel.PostAsync"/> call through the <c>Func&lt;IPublishedNoteSink?&gt;</c>
    /// it was built with, so switching tabs or columns takes effect on the very next post with no extra
    /// event wiring between this class and that one.
    /// </summary>
    public IPublishedNoteSink? ActiveSink => SelectedTab?.SelectedColumn ?? SelectedTab?.Columns.FirstOrDefault();

    /// <summary>Adds a new tab (with one column, same as the default) and selects it. Plan §11.5: no name-edit
    /// UI, no persistence -- "Tab N" for the process's lifetime.</summary>
    public TabViewModel AddTab()
    {
        var tab = NewTab($"Tab {Tabs.Count + 1}");
        Tabs.Add(tab);
        SelectedTab = tab;
        return tab;
    }

    /// <summary>Closes <paramref name="tab"/>, disposing every column under it (plan §11.3). Refuses to close
    /// the last tab (plan §11.5: there is no empty-workspace recovery UI to build for v1) and refuses a tab
    /// this workspace does not have -- both are no-ops, not exceptions, since a stale button click has nothing
    /// useful to report back for either case.</summary>
    public void CloseTab(TabViewModel tab)
    {
        if (Tabs.Count <= 1 || !Tabs.Remove(tab))
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedTab, tab);
        tab.Dispose();

        if (wasSelected)
        {
            SelectedTab = Tabs[^1];
        }
    }

    private TabViewModel NewTab(string title)
    {
        var tab = new TabViewModel(title, _columnFactory);
        tab.AddColumn();
        return tab;
    }

    /// <summary>Retries exactly the columns, across every tab, whose last <see cref="TimelineViewModel.Subscribe"/>
    /// failed because it needed a signed-in account and none was active yet
    /// (<see cref="TimelineViewModel.NeedsAccountRetry"/>). Called once the key store reports an active
    /// signer (plan §18.2 item 4): "press Subscribe again" is not a requirement a user can see, so this does it
    /// for them. A column whose query never needed an account is untouched -- re-subscribing it would just
    /// restart an already-working stream for no reason.</summary>
    public void NotifyAccountUnlocked()
    {
        foreach (var tab in Tabs)
        {
            foreach (var column in tab.Columns)
            {
                if (column.NeedsAccountRetry)
                {
                    column.Subscribe();
                }
            }
        }
    }

    /// <summary>Symmetric counterpart of <see cref="NotifyAccountUnlocked"/>: when the key store locks or signs
    /// out, every column currently bound to that account (<see cref="TimelineViewModel.IsAccountBound"/>)
    /// is re-subscribed so it falls back to <see cref="ITimelineSourceFactory.CreateAnonymous"/> instead of
    /// silently continuing to show a feed for an account that is no longer unlocked.</summary>
    public void NotifyAccountLocked()
    {
        foreach (var tab in Tabs)
        {
            foreach (var column in tab.Columns)
            {
                if (column.IsAccountBound)
                {
                    column.Subscribe();
                }
            }
        }
    }

    /// <summary>Disposes every tab (plan §11.3: closing the window closes everything under the workspace).</summary>
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
