using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Theming;

namespace NightlyDawn.App.Views;

public sealed partial class ThemeSwitcherView : UserControl
{
    public ThemeSwitcherView() => AvaloniaXamlLoader.Load(this);

    /// <summary>Wires the view model. Unlike <see cref="TabStripView"/> and <see cref="WorkspaceView"/> this one
    /// cannot inherit the window's DataContext: the window's is the workspace, and the theme is not part of it.
    /// Same explicit-attach shape <see cref="KeysPanelView"/> uses.</summary>
    public void Attach(ThemeSwitcherViewModel theme) => DataContext = theme;
}
