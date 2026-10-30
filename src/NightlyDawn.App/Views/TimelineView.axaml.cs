using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

/// <summary>
/// One tab's timeline, plus its keyboard (L17, plan §68.9). The fork's <c>[Timeline]</c> key group
/// (<c>StarryEyes/Settings/KeyAssigns/DefaultAssignProvider.cs:26-72</c>) is mapped here as far as a single tab
/// reaches:
///
/// <code>
/// down, j   FocusDown      up, k   FocusUp       s+j  FocusBottom     s+k, 0  FocusTop     escape  ClearFocus
/// r         Reply          c+r     Repost        f, Oem1  React (♥)   c+c     copy the note's text
/// </code>
///
/// <para>Deliberately <b>not</b> implemented, with the mechanism recorded so nobody re-reads the key table and
/// files a bug: <c>c+t: CreateTab</c> and <c>c+q: QuoteLink</c> are unreachable in the fork itself. Its
/// <c>[Global]</c> group runs first on the window's <c>PreviewKeyDown</c> (<c>KeyAssignTrigger.cs:28-36</c>),
/// <c>GetAssigns</c> only ever consults the group it was asked for (<c>KeyAssignProfile.cs:157-167</c>), and
/// <c>[Global]</c> already binds <c>c+t</c> to <c>FocusToTimeline</c> and <c>c+q</c> to <c>FocusToSearch</c> --
/// so the timeline bindings for those chords were never reached (plan §37.3's "the fork's real inconsistency,
/// reproduced rather than repaired"). Tab/column moves (<c>h/l</c>, <c>s+h/s+l</c>), <c>space: ToggleSelect</c>
/// and every bulk action are a different leaf: they touch <c>WorkspaceViewModel</c>/<c>ColumnViewModel</c> or
/// need multi-select.</para>
///
/// <para>Two rules of the wiring, both Lead rulings (plan §68.9.1): (1) the cursor is <see cref="TimelineViewModel.FocusedIndex"/>
/// and nothing else -- <c>TimelineList.SelectedIndex</c> is bound to it two-way in the XAML, so a mouse click
/// and a keypress update the same number, and the row highlight is the list's own selected look. (2) exactly
/// one thing moves on <c>↑</c>/<c>↓</c>: the handler below takes them and marks them handled, so the
/// <c>ListBox</c>'s built-in arrow handling never also runs (otherwise one keypress would move two rows). Keys
/// this handler leaves alone (Home/End/PageUp/PageDown) still go through the same bound index.</para>
///
/// <para>The handler is attached to the list on the <b>tunnel</b> pass, like <c>SearchBoxView.axaml.cs:22</c>: a
/// bubbling handler would see <c>↑</c>/<c>↓</c> only after the <c>ListBoxItem</c> had already acted on them.
/// Attaching it to <c>TimelineList</c> rather than the window is also what keeps the fork's text-box rule
/// (<c>KeyAssignTrigger.cs:55-95</c>) for free: <c>QueryBox</c>, <c>ComposeText</c> and the search box are not
/// descendants of the list, so a key typed into any of them never routes through here, and <c>r</c> in the
/// compose box types an <c>r</c>. The <c>TextBox</c> check inside the handler is the belt to that suspenders,
/// for a future row template that grows an input.</para>
/// </summary>
public sealed partial class TimelineView : UserControl
{
    private readonly ListBox _list;

    public TimelineView()
    {
        AvaloniaXamlLoader.Load(this);
        _list = this.FindControl<ListBox>("TimelineList")
            ?? throw new InvalidOperationException("TimelineView.axaml no longer has a ListBox named TimelineList");
        _list.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    private TimelineViewModel? Tab => DataContext as TimelineViewModel;

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (Tab is not { } tab || e.Source is TextBox)
        {
            return;
        }

        // Exact modifier matches, as in the fork (`b.Modifiers == modifiers`, KeyAssignTrigger.cs:97): `c+r` must
        // not also fire on `c+s+r` (the fork's RetweetMany, out of scope), and a bare `j` must not fire on `s+j`.
        switch (e.KeyModifiers)
        {
            case KeyModifiers.None:
                e.Handled = HandleUnmodified(tab, e.Key);
                break;
            case KeyModifiers.Shift:
                e.Handled = HandleShifted(tab, e.Key);
                break;
            case KeyModifiers.Control:
                e.Handled = HandleControl(tab, e.Key);
                break;
            default:
                break;
        }
    }

    private bool HandleUnmodified(TimelineViewModel tab, Key key)
    {
        switch (key)
        {
            case Key.Down:
            case Key.J:
                MoveCursor(tab, tab.FocusDown);
                return true;
            case Key.Up:
            case Key.K:
                MoveCursor(tab, tab.FocusUp);
                return true;
            case Key.D0:
                MoveCursor(tab, tab.FocusTop);
                return true;
            case Key.Escape:
                tab.ClearFocus();
                return true;
            case Key.R:
                tab.ReplyToFocused();
                return true;
            case Key.F:
            case Key.OemSemicolon: // The fork's `Oem1` (WPF Key.Oem1 == Key.OemSemicolon; Avalonia names it the same way).
                RunAction(tab.ReactToFocusedAsync());
                return true;
            default:
                return false;
        }
    }

    private bool HandleShifted(TimelineViewModel tab, Key key)
    {
        switch (key)
        {
            case Key.J:
                MoveCursor(tab, tab.FocusBottom);
                return true;
            case Key.K:
                MoveCursor(tab, tab.FocusTop);
                return true;
            default:
                return false;
        }
    }

    private bool HandleControl(TimelineViewModel tab, Key key)
    {
        switch (key)
        {
            case Key.R:
                RunAction(tab.RepostFocusedAsync());
                return true;
            case Key.C:
                CopyFocusedAsync(tab);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Fork <c>FocusedStatus</c>'s setter -> <c>ScrollIntoViewMessage(index)</c> -> <c>BringIndexIntoViewPublic</c>
    /// (<c>TimelineViewModelBase.cs:354-356</c>, <c>ScrollIntoViewInteractionMessageAction.cs:10-18</c>): the row the
    /// key moved to is brought on screen, and keyboard focus moves onto its container so the next key still routes
    /// through this list -- a container that scrolls out of the realized range would otherwise take focus with it
    /// (<see cref="ListBoxFocus"/>). <c>UpdateLayout</c> between the two is what makes the container exist to focus,
    /// the same way <c>SearchFlipView.FocusResults</c> does it.</summary>
    private void MoveCursor(TimelineViewModel tab, Action move)
    {
        move();
        var index = tab.FocusedIndex;
        if (index < 0)
        {
            return;
        }

        _list.ScrollIntoView(index);
        _list.UpdateLayout();
        ListBoxFocus.FocusRow(_list, index);
    }

    /// <summary>Observes the publish task so a fault could never go unobserved. <see cref="Composing.NoteRowActions"/>
    /// catches every publisher exception and reports it on the tab's Status (plan §10.4-2), so in practice the only
    /// thing reaching this catch would be a bug in that class; it is still reported rather than swallowed.</summary>
    private async void RunAction(Task action)
    {
        try
        {
            await action;
        }
        catch (Exception ex)
        {
            Tab?.ReportStatus($"Action failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>The fork's <c>c+c: Copy</c>. Writes only on this explicit keypress (the same rule as every clipboard
    /// write in <c>KeysPanelView</c>), and only the note's text -- relay-supplied, but what the person asked for.
    /// Every outcome lands on the tab's Status, including a platform with no clipboard.</summary>
    private async void CopyFocusedAsync(TimelineViewModel tab)
    {
        var text = tab.FocusedContentForCopy();
        if (text is null)
        {
            return; // FocusedContentForCopy already said why on Status.
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                tab.ReportStatus("Copy failed: no clipboard is available here.");
                return;
            }

            await clipboard.SetTextAsync(text);
            tab.ReportStatus("Note text copied to the clipboard.");
        }
        catch (Exception ex)
        {
            tab.ReportStatus($"Copy failed ({ex.GetType().Name}).");
        }
    }
}
