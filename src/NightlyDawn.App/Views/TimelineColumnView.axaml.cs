using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class TimelineColumnView : UserControl
{
    public TimelineColumnView() => AvaloniaXamlLoader.Load(this);

    // DataContext is inherited from the host window (MainWindow sets its own DataContext to the
    // TimelineColumnViewModel), not set here -- the extraction must not change what the column binds to.
    private TimelineColumnViewModel? ViewModel => DataContext as TimelineColumnViewModel;

    private void OnSubscribeClick(object? sender, RoutedEventArgs e) => ViewModel?.Subscribe();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => ViewModel?.RequestClose?.Invoke();
}
