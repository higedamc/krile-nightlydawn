using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class NoteRowView : UserControl
{
    public NoteRowView() => AvaloniaXamlLoader.Load(this);

    private NoteRow? Row => DataContext as NoteRow;

    // Reply/Quote only hand the target to the shared compose box (NoteRowActions.BeginReply/BeginQuote are
    // synchronous) -- the publish itself happens later, when that box's own Post is pressed.
    private void OnReplyClick(object? sender, RoutedEventArgs e) => Row?.Actions?.BeginReply();

    private void OnQuoteClick(object? sender, RoutedEventArgs e) => Row?.Actions?.BeginQuote();

    private async void OnRepostClick(object? sender, RoutedEventArgs e) => await (Row?.Actions?.RepostAsync() ?? Task.CompletedTask);

    private async void OnReactClick(object? sender, RoutedEventArgs e) => await (Row?.Actions?.ReactAsync() ?? Task.CompletedTask);
}
