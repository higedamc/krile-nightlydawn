using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class WorkspaceView : UserControl
{
    public WorkspaceView() => AvaloniaXamlLoader.Load(this);

    // DataContext is inherited from the host window (MainWindow sets its own DataContext to the
    // WorkspaceViewModel), not set here -- same pattern TimelineColumnView used before this leaf.
    private WorkspaceViewModel? ViewModel => DataContext as WorkspaceViewModel;

    private void OnAddTabClick(object? sender, RoutedEventArgs e) => ViewModel?.AddTab();

    private void OnSelectTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabViewModel tab } && ViewModel is { } workspace)
        {
            workspace.SelectedTab = tab;
        }
    }

    private void OnCloseTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TabViewModel tab })
        {
            ViewModel?.CloseTab(tab);
        }
    }

    private void OnAddColumnClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectedTab?.AddColumn();

    private void OnCloseColumnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TimelineColumnViewModel column })
        {
            ViewModel?.SelectedTab?.RemoveColumn(column);
        }
    }
}
