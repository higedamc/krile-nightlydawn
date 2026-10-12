using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class TimelineView : UserControl
{
    public TimelineView() => AvaloniaXamlLoader.Load(this);

    // DataContext is inherited from the host window (MainWindow sets its own DataContext to the
    // TimelineViewModel), not set here -- the extraction must not change what the column binds to.
    private TimelineViewModel? ViewModel => DataContext as TimelineViewModel;

    private void OnSubscribeClick(object? sender, RoutedEventArgs e) => ViewModel?.Subscribe();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => ViewModel?.RequestClose?.Invoke();
}
