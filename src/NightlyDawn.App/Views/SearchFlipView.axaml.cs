using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Search;

namespace NightlyDawn.App.Views;

public sealed partial class SearchFlipView : UserControl
{
    private readonly ListBox _resultList;
    private SearchFlipViewModel? _search;

    public SearchFlipView()
    {
        AvaloniaXamlLoader.Load(this);
        _resultList = this.FindControl<ListBox>("SearchResultList")!;
        // Fork SearchResultViewModel.cs:50-53: "back" from inside the results rewinds the stack. Tunnel for the
        // same reason SearchBoxView tunnels: ListBox handles keys of its own on the bubbling pass.
        _resultList.AddHandler(KeyDownEvent, OnResultListKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Same explicit-attach shape as <see cref="ThemeSwitcherView"/> and <see cref="SearchBoxView"/>.</summary>
    public void Attach(SearchFlipViewModel search)
    {
        ArgumentNullException.ThrowIfNull(search);
        _search = search;
        DataContext = search;
    }

    /// <summary>What <see cref="SearchFocusTarget.Results"/> resolves to (fork: <c>SearchResult.SetFocus()</c> after
    /// Enter). Selects the first row when nothing is selected and focuses that row's container
    /// (<see cref="ListBoxFocus.FocusRow"/>: the list itself cannot take focus), so the arrow keys have somewhere
    /// to start.</summary>
    public bool FocusResults()
    {
        // Enter in query mode builds the result and asks for focus in the same call, before any layout pass has
        // realized a row container; without this the focus request finds no container and goes nowhere (the
        // keyword mode never showed it, because its rows were realized while the user was still typing).
        _resultList.UpdateLayout();
        if (_resultList.SelectedIndex < 0 && _resultList.ItemCount > 0)
        {
            _resultList.SelectedIndex = 0;
        }

        return ListBoxFocus.FocusRow(_resultList, _resultList.SelectedIndex);
    }

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        _search?.Close();
        e.Handled = true;
    }

    private void OnResultAreaSizeChanged(object? sender, SizeChangedEventArgs e) =>
        _search?.NotifyResultWidthChanged(e.NewSize.Width);

    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SearchModeOption option } && _search is not null)
        {
            _search.SearchMode = option.Mode;
        }
    }

    private void OnResultListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _search is not null)
        {
            _search.RewindStack();
            e.Handled = true;
        }
    }
}
