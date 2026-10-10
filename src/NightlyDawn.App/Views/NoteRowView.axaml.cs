using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NightlyDawn.App.Views;

public sealed partial class NoteRowView : UserControl
{
    public NoteRowView() => AvaloniaXamlLoader.Load(this);
}
