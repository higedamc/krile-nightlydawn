using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Search;

namespace NightlyDawn.App.Views;

public sealed partial class SearchBoxView : UserControl
{
    private readonly TextBox _searchBox;
    private SearchFlipViewModel? _search;

    public SearchBoxView()
    {
        AvaloniaXamlLoader.Load(this);
        _searchBox = this.FindControl<TextBox>("SearchBox")!;
        _searchBox.GotFocus += OnSearchBoxGotFocus;
        // Tunnel, not bubble: TextBox's own OnKeyDown runs on the bubbling pass and would consume ↑/↓ (caret
        // moves) before a bubbling handler here ever saw them, and an unhandled Enter would go on to the window's
        // IsDefault button lookup (TabStripView's SubscribeButton while its popup is open).
        _searchBox.AddHandler(KeyDownEvent, OnSearchBoxKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Same explicit-attach shape as <see cref="ThemeSwitcherView"/>: the window's DataContext is the
    /// workspace, and the search flip is not part of it.</summary>
    public void Attach(SearchFlipViewModel search)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
        DataContext = search;
    }

    /// <summary>What <see cref="SearchFocusTarget.SearchBox"/> resolves to.</summary>
    public bool FocusInput() => _searchBox.Focus();

    /// <summary>Fork MainWindow.xaml:128-130 -> <c>GotFocusToSearchBox</c> -> <c>Open()</c> (plan §37.2 item 1): the
    /// panel opens the moment the box has focus, before anything is typed.</summary>
    private void OnSearchBoxGotFocus(object? sender, RoutedEventArgs e) => _search?.Open();

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_search is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                _search.OnEnterKeyDown();
                e.Handled = true;
                break;
            case Key.Escape:
                _search.RewindStack();
                e.Handled = true;
                break;
            case Key.Down when _search.IsSearchOptionAvailable:
                _search.SetNextSearchOption();
                e.Handled = true;
                break;
            case Key.Up when _search.IsSearchOptionAvailable:
                _search.SetPreviousSearchOption();
                e.Handled = true;
                break;
            default:
                break;
        }
    }
}
