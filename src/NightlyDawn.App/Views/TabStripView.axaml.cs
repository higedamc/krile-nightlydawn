using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class TabStripView : UserControl
{
    public TabStripView() => AvaloniaXamlLoader.Load(this);

    // DataContext is inherited from the enclosing DataTemplate item (plan §21.2: now the ColumnViewModel
    // WorkspaceView's per-column template is bound to), not set here -- same pattern WorkspaceView and
    // TimelineView use.
    private ColumnViewModel? ViewModel => DataContext as ColumnViewModel;

    private void OnAddTabClick(object? sender, RoutedEventArgs e) => ViewModel?.AddTab();

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
}
