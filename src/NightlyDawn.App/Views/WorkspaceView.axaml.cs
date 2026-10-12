using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class WorkspaceView : UserControl
{
    public WorkspaceView() => AvaloniaXamlLoader.Load(this);

    // DataContext is inherited from the host window (MainWindow sets its own DataContext to the
    // WorkspaceViewModel), not set here -- same pattern TimelineView used before this leaf.
    private WorkspaceViewModel? ViewModel => DataContext as WorkspaceViewModel;

    private void OnAddColumnClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectedTab?.AddColumn();
}
