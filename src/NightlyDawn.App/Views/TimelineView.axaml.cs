using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NightlyDawn.App.Views;

public sealed partial class TimelineView : UserControl
{
    public TimelineView() => AvaloniaXamlLoader.Load(this);
}
