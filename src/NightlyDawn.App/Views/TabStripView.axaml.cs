using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class TabStripView : UserControl
{
    private readonly ToggleButton _settingsToggle;

    public TabStripView()
    {
        AvaloniaXamlLoader.Load(this);
        _settingsToggle = this.FindControl<ToggleButton>("SettingsToggle")!;
    }

    // DataContext is inherited from the enclosing DataTemplate item (plan §21.2: now the ColumnViewModel
    // WorkspaceView's per-column template is bound to), not set here -- same pattern WorkspaceView and
    // TimelineView use.
    private ColumnViewModel? ViewModel => DataContext as ColumnViewModel;

    // Opens the settings popup immediately (plan §26.5 / L4c ④) by setting the single source of truth
    // (SettingsToggle.IsChecked -- TabSettingsPanel's Popup.IsOpen binds to it in XAML, plan §28 fix): a
    // freshly built tab is unsubscribed, and the gear toggle is the only other way in -- without this, a
    // tab a user just created would look like dead UI until they noticed the gear.
    private void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddTab();
        _settingsToggle.IsChecked = true;
    }

    private void OnSelectTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TimelineViewModel tab } && ViewModel is { } column)
        {
            column.SelectedTab = tab;
        }
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TimelineViewModel tab })
        {
            ViewModel?.CloseTab(tab);
        }
    }

    private void OnSubscribeClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectedTab?.Subscribe();

    private void OnCloseSelectedTabClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectedTab?.RequestClose?.Invoke();
}
