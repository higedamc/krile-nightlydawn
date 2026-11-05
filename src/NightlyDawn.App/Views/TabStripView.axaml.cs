using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class TabStripView : UserControl
{
    /// <summary>Which of the fork's three tab colours a header takes (L18; see TabStripView.axaml's styles for why
    /// these are two conditions and not three independent states). An attached property so a plain style selector can
    /// key off it; set only by <see cref="TabHeaderRoleConverter"/> through a MultiBinding.</summary>
    public static readonly AttachedProperty<TabHeaderRole> RoleProperty =
        AvaloniaProperty.RegisterAttached<TabStripView, Button, TabHeaderRole>("Role");

    public static TabHeaderRole GetRole(Button button) => button.GetValue(RoleProperty);

    public static void SetRole(Button button, TabHeaderRole value) => button.SetValue(RoleProperty, value);

    private readonly ToggleButton _settingsToggle;

    public TabStripView()
    {
        AvaloniaXamlLoader.Load(this);
        _settingsToggle = this.FindControl<ToggleButton>("SettingsToggle")!;
    }

    // DataContext is inherited from the enclosing DataTemplate item (plan §21.2: now the ColumnViewModel
    // WorkspaceView's per-column template is bound to), not set here -- same pattern WorkspaceView and
    // TimelineView use.
    private ColumnViewModel? ViewModel => DataContext as ColumnViewModel;

    // Opens the settings popup immediately (plan §26.5 / L4c ④) by setting the single source of truth
    // (SettingsToggle.IsChecked -- TabSettingsPanel's Popup.IsOpen binds to it in XAML, plan §28 fix): a
    // freshly built tab is unsubscribed, and the gear toggle is the only other way in -- without this, a
    // tab a user just created would look like dead UI until they noticed the gear.
    private void OnAddTabClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.AddTab();
        _settingsToggle.IsChecked = true;
    }

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

    private void OnSubscribeClick(object? sender, RoutedEventArgs e) => ViewModel?.SelectedTab?.Subscribe();

    private void OnCloseSelectedTabClick(object? sender, RoutedEventArgs e) =>
        ViewModel?.SelectedTab?.RequestClose?.Invoke();
}

/// <summary>The fork's tab colour roles (StarryEyes <c>MainArea.xaml:66-81</c>).</summary>
public enum TabHeaderRole
{
    /// <summary>Not the column's selected tab.</summary>
    Idle,

    /// <summary>The column's selected tab, but the column is not the focused one ("TabSelected").</summary>
    Selected,

    /// <summary>The column's selected tab and the column is the focused one ("TabFocused").</summary>
    Focused,
}

/// <summary>Inputs, in order: the column's <c>SelectedTab</c>, this header's own tab, the column's <c>IsFocused</c>.
/// Anything not yet resolved (a binding still unset, or in error) is <see cref="TabHeaderRole.Idle"/> -- the
/// quietest colour, never a wrong loud one.</summary>
public sealed class TabHeaderRoleConverter : IMultiValueConverter
{
    public static readonly TabHeaderRoleConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count != 3 || values[0] is BindingNotification or UnsetValueType || values[1] is null || values[2] is not bool columnFocused)
        {
            return TabHeaderRole.Idle;
        }

        if (!ReferenceEquals(values[0], values[1]))
        {
            return TabHeaderRole.Idle;
        }

        return columnFocused ? TabHeaderRole.Focused : TabHeaderRole.Selected;
    }
}
