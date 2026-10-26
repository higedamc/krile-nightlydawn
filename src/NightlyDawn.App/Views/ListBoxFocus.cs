using Avalonia.Controls;

namespace NightlyDawn.App.Views;

/// <summary>
/// Moves keyboard focus into a <see cref="ListBox"/> the way a click on one of its rows would (L7a, plan §37.2
/// items 6 and 8). A <see cref="ListBox"/> is not focusable itself in Avalonia -- its <see cref="ListBoxItem"/>
/// containers are -- so <c>list.Focus()</c> returns false and leaves focus where it was, which is how the first
/// draft of the search flip's "Enter moves focus to the results" was green on a flag and red on the real focus
/// manager. Focusing a realized container is what makes <c>IsKeyboardFocusWithin</c> true on the list and gives
/// the arrow keys a row to start from.
/// </summary>
internal static class ListBoxFocus
{
    /// <summary>Focuses the container of <paramref name="index"/> (clamped to the realized rows), or the list
    /// itself when it has no rows. Returns whether anything took focus.</summary>
    public static bool FocusRow(ListBox list, int index)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (list.ItemCount > 0)
        {
            var clamped = Math.Clamp(index, 0, list.ItemCount - 1);
            if (list.ContainerFromIndex(clamped) is { } container && container.Focus())
            {
                return true;
            }
        }

        return list.Focus();
    }
}
