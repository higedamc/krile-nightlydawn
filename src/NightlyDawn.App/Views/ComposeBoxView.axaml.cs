using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Composing;

namespace NightlyDawn.App.Views;

public sealed partial class ComposeBoxView : UserControl
{
    public ComposeBoxView() => AvaloniaXamlLoader.Load(this);

    private ComposeViewModel? ViewModel => DataContext as ComposeViewModel;

    // async void: ComposeViewModel.PostAsync never lets an exception escape -- it catches INotePublisher's two
    // documented failure modes (ArgumentException, EventPublishException) plus a trailing catch (Exception)
    // for everything else (plan §10.4-2), so there is nothing left for this handler to catch.
    private async void OnPostClick(object? sender, RoutedEventArgs e) => await (ViewModel?.PostAsync() ?? Task.CompletedTask);

    private void OnCancelTargetClick(object? sender, RoutedEventArgs e) => ViewModel?.CancelTargeting();
}
