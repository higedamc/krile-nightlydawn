using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;

namespace NightlyDawn.App.Search;

/// <summary>Where the view should move keyboard focus, raised through <see cref="SearchFlipViewModel.FocusRequested"/>.
/// The view model has no Avalonia dependency, so it names the target and <c>MainWindow</c> resolves it.</summary>
public enum SearchFocusTarget
{
    /// <summary>The search text box in the header (fork: <c>FocusToSearchBox</c>).</summary>
    SearchBox,

    /// <summary>The result list inside the open panel (fork: <c>SearchResult.SetFocus()</c> after Enter).</summary>
    Results,

    /// <summary>The selected tab's timeline (fork: <c>MainWindowModel.SetFocusTo(FocusRequest.Timeline)</c> on close).</summary>
    Timeline,
}

/// <summary>
/// The search flip's state machine, ported from StarryEyes <c>ViewModels/WindowParts/Flips/SearchFlipViewModel.cs</c>
/// (482 lines, read in full; plan §37.2 fixes the eleven behaviours this reproduces). The fork's answer to "what
/// does the search box do" is neither a new tab nor a filter on the visible timeline (??? on 2026-10-26, plan §37):
/// focusing the box opens a panel over the workspace, results appear inside it, and pinning them to a tab is a
/// separate command (L7b). L7a is the offline nine-tenths of that: <see cref="SearchMode.CurrentTab"/> searching
/// as you type, the <c>?</c> query mode through the existing KQL compiler, Enter, the back stack, the close-time
/// cleanup and the slim threshold. The other four modes exist so the cycle and the stack are the fork's, and say
/// "not implemented" when committed (plan §37.5 gives their contents to L7c).
///
/// <para>No Avalonia here, like every other view model in this assembly: focus moves are requested through
/// <see cref="FocusRequested"/>, text arrives through <see cref="Text"/>, and the view calls the same methods the
/// fork's XAML triggers called (<see cref="Open"/> on GotFocus, <see cref="OnEnterKeyDown"/>, <see cref="RewindStack"/>,
/// <see cref="NotifyResultWidthChanged"/>). Everything runs on the UI thread, synchronously -- the fork compiled a
/// <c>?</c> query on a background task because its compiler hit the database; ours is a tokenizer over a string the
/// text box has already bounded to <see cref="AppState.MaxQueryLength"/>.</para>
///
/// <para><b>Scope of a result in L7a.</b> Both <see cref="SearchMode.CurrentTab"/> and the <c>?</c> query mode
/// evaluate against the notes the selected tab is showing right now, through <see cref="TimelineViewModel.FindNote"/>.
/// The fork's query mode searched its whole local database instead; that scope is <see cref="SearchMode.Local"/>'s
/// and lands with it (plan §37.4: no persistent event cache exists yet). A <c>?</c> query's relay-side half
/// (<c>CompiledFilter.RelayFilter</c>) is therefore never sent from here -- only its <c>LocalPredicate</c> runs,
/// which plan §7.2 guarantees re-evaluates the whole query locally.</para>
/// </summary>
public sealed class SearchFlipViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Below this result-area width the panel drops its mode descriptions (fork: <c>DisplaySlimView = width &lt; 572</c>,
    /// plan §37.2 item 9). The fork's number, kept as is.</summary>
    public const double SlimWidthThreshold = 572;

    /// <summary>The fork's <c>SearchFlipQueryEmpty</c> resource string.</summary>
    public const string QueryEmptyText = "Query is empty.";

    /// <summary>The fork's <c>SearchFlipQueryHelp</c>, extended with the keys this port binds (the fork's were
    /// user-assignable key assigns, which this app does not have yet).</summary>
    public const string KeywordHelpText =
        "Searches this tab's notes as you type: words are OR-ed, -word excludes. Start with ? for a KQL query. " +
        "↑/↓ change where to search, Enter jumps to the results, Esc goes back.";

    /// <summary>The fork's <c>SearchFlipQueryValid</c> resource string.</summary>
    public const string QueryValidText = "Query is valid. Press Enter to search this tab's notes with it.";

    /// <summary>Plan §37.4's Nostr replacement for the fork's <c>^@?[A-Za-z0-9_]+$</c> screen-name test: an npub
    /// (<c>npub1</c> + 58 bech32 characters), an nprofile, a 64-digit hex pubkey, or a NIP-05 <c>name@domain</c>.
    /// Anchored and linear -- no nested quantifiers -- and the input is already capped at
    /// <see cref="AppState.MaxQueryLength"/> by the text box, so the match timeout is belt-and-braces.</summary>
    private static readonly Regex UserIdentifierRegex = new(
        @"^(npub1[02-9ac-hj-np-z]{58}|nprofile1[02-9ac-hj-np-z]{6,}|[0-9a-fA-F]{64}|[A-Za-z0-9._+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)+)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    private readonly Func<TimelineViewModel?> _currentTab;
    private readonly Func<ColumnViewModel?> _currentColumn;
    private readonly IFilterCompiler _compiler;
    private readonly Stack<(string Text, SearchMode Mode)> _backStack = new();
    private readonly SearchModeOption[] _modeOptions;

    private bool _isOpen;
    private string _text = string.Empty;
    private string? _previousCommit;
    private SearchMode _searchMode = SearchMode.CurrentTab;
    private bool _isQueryMode;
    private bool _isSearchOptionAvailable;
    private bool _isSearchResultAvailable;
    private bool _canBeUserScreenName;
    private bool _displaySlimView;
    private string? _errorText;
    private string? _unavailableText;
    private string? _pinError;
    private SearchResultViewModel? _searchResult;
    private Func<Note, bool>? _compiledQuery;
    private bool _disposed;

    /// <param name="currentTab">Resolves the tab a <see cref="SearchMode.CurrentTab"/> search runs over, read fresh at
    /// every commit (the fork read <c>TabManager.CurrentFocusTab</c> the same way). Null means no tab is selected.</param>
    /// <param name="currentColumn">Resolves the column <see cref="PinToTab"/> adds its new tab to (L7b) -- the column
    /// that owns <paramref name="currentTab"/>'s tab, read fresh at pin time the same way. Null means no column is
    /// selected.</param>
    /// <param name="compiler">The KQL compiler the <c>?</c> query mode validates and compiles with -- the same one the
    /// relay layer uses for tab queries, so "valid here" and "valid as a tab query" are one judgement. Also what
    /// <see cref="PinToTab"/> parses the current tab's own query with, to AND it with the keyword expression.</param>
    public SearchFlipViewModel(Func<TimelineViewModel?> currentTab, Func<ColumnViewModel?> currentColumn, IFilterCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(currentTab);
        ArgumentNullException.ThrowIfNull(currentColumn);
        ArgumentNullException.ThrowIfNull(compiler);
        _currentTab = currentTab;
        _currentColumn = currentColumn;
        _compiler = compiler;
        _modeOptions =
        [
            new SearchModeOption(SearchMode.CurrentTab, "This tab", "Search the notes this tab is showing. Updates as you type."),
            new SearchModeOption(SearchMode.Local, "Loaded notes", "Search every note any tab holds right now. Not implemented yet (L7c)."),
            new SearchModeOption(SearchMode.Web, "Relays", "Ask relays that support NIP-50 search. Not implemented yet (L7c)."),
            new SearchModeOption(SearchMode.UserWeb, "People", "Search profiles on relays. Not implemented yet (L7c)."),
            new SearchModeOption(SearchMode.UserScreenName, "Open user", "Open this npub / nprofile / NIP-05 / hex pubkey. Not implemented yet (L7c)."),
        ];
        RefreshModeOptions();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The view moves keyboard focus where this says (see <see cref="SearchFocusTarget"/>).</summary>
    public event Action<SearchFocusTarget>? FocusRequested;

    /// <summary>Whether the panel is over the workspace (fork: <c>IsVisible</c> of the flip). Set by <see cref="Open"/>
    /// and <see cref="Close"/> only.</summary>
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetField(ref _isOpen, value);
    }

    /// <summary>The search box's text, two-way. Setting it runs the fork's <c>OnTextChanged</c> (plan §37.2 items 3
    /// and 4): a <c>?</c> prefix compiles the rest as KQL and reports in <see cref="ErrorText"/>; otherwise, in
    /// <see cref="SearchMode.CurrentTab"/>, every change commits a search immediately. Also opens the panel if text
    /// arrives while it is closed -- keystrokes must never produce results nobody can see.</summary>
    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnTextChanged(value);
            RaisePropertyChanged();
        }
    }

    /// <summary>Changing the mode re-commits at once (fork :249, plan §37.2 item 5) -- including to a "not implemented"
    /// result for the L7c modes, so choosing one is answered, not ignored.</summary>
    public SearchMode SearchMode
    {
        get => _searchMode;
        set
        {
            if (_searchMode == value)
            {
                return;
            }

            _searchMode = value;
            RaisePropertyChanged();
            RefreshModeOptions();
            CommitSearch();
        }
    }

    /// <summary>True while <see cref="Text"/> starts with <c>?</c>: the rest is KQL, the mode list is hidden and
    /// nothing searches until Enter (fork :276-300).</summary>
    public bool IsQueryMode
    {
        get => _isQueryMode;
        private set => SetField(ref _isQueryMode, value);
    }

    /// <summary>Whether the mode list is shown: text present and not in query mode (fork :304-321).</summary>
    public bool IsSearchOptionAvailable
    {
        get => _isSearchOptionAvailable;
        private set => SetField(ref _isSearchOptionAvailable, value);
    }

    /// <summary>Whether the result area has something to show -- a <see cref="SearchResult"/>, or
    /// <see cref="UnavailableText"/> for a mode L7a does not implement.</summary>
    public bool IsSearchResultAvailable
    {
        get => _isSearchResultAvailable;
        private set => SetField(ref _isSearchResultAvailable, value);
    }

    /// <summary>Whether <see cref="SearchMode.UserScreenName"/> is offered in the cycle: the text looks like a Nostr user
    /// identifier (see <see cref="UserIdentifierRegex"/>; fork :352, with Twitter's rule).</summary>
    public bool CanBeUserScreenName
    {
        get => _canBeUserScreenName;
        private set
        {
            if (SetField(ref _canBeUserScreenName, value))
            {
                RefreshModeOptions();
            }
        }
    }

    /// <summary>Fork :162-165: true once the result area is narrower than <see cref="SlimWidthThreshold"/>. The view
    /// hides the mode descriptions in slim mode; the error text and the mode names stay.</summary>
    public bool DisplaySlimView
    {
        get => _displaySlimView;
        private set
        {
            if (SetField(ref _displaySlimView, value))
            {
                RefreshModeOptions();
            }
        }
    }

    /// <summary>In query mode, why the KQL does not compile (or <see cref="QueryEmptyText"/> for a bare <c>?</c>);
    /// null when it compiles, and always null outside query mode.</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetField(ref _errorText, value))
            {
                RaisePropertyChanged(nameof(HasError));
                RaisePropertyChanged(nameof(QueryStatusText));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    /// <summary>The one line under the box: the keyword help outside query mode, <see cref="QueryValidText"/>,
    /// <see cref="QueryEmptyText"/> for a bare <c>?</c>, or the compile error inside it (fork: three
    /// separately-toggled TextBlocks in <c>SearchFlip.xaml:349-368</c>). An empty query is not "invalid": there is
    /// nothing to be wrong yet, so it is reported in the fork's own words.</summary>
    public string QueryStatusText => !IsQueryMode
        ? KeywordHelpText
        : !HasError ? QueryValidText
        : ErrorText == QueryEmptyText ? QueryEmptyText
        : $"Query is invalid: {ErrorText}";

    /// <summary>Set instead of <see cref="SearchResult"/> when the committed mode has no implementation yet, so the
    /// result area says so where the results would have been. Null whenever a real result (or nothing) is showing.</summary>
    public string? UnavailableText
    {
        get => _unavailableText;
        private set => SetField(ref _unavailableText, value);
    }

    /// <summary>Why the last <see cref="PinToTab"/> call pinned nothing, shown next to the Pin button (plan §37's
    /// "a mode that can be chosen must answer, not do nothing" applies to the button too, plan §41 item ⑤: a full
    /// column must say so, not swallow the click). Null once nothing is wrong -- cleared by the next commit and by
    /// <see cref="PinToTab"/> succeeding, but not merely by editing <see cref="Text"/>, so the reason stays visible
    /// until the person does something about it.</summary>
    public string? PinError
    {
        get => _pinError;
        private set => SetField(ref _pinError, value);
    }

    /// <summary>The live result of the last commit, or null. Replacing it disposes the previous one (fork :86-93).</summary>
    public SearchResultViewModel? SearchResult
    {
        get => _searchResult;
        private set
        {
            if (ReferenceEquals(_searchResult, value))
            {
                return;
            }

            var previous = _searchResult;
            _searchResult = value;
            RaisePropertyChanged();
            previous?.Dispose();
        }
    }

    /// <summary>The five modes as the view lists them (fork <c>SearchFlip.xaml:380-510</c>): selection, visibility
    /// (<see cref="SearchMode.UserScreenName"/> only while <see cref="CanBeUserScreenName"/>) and whether the
    /// description is shown (<see cref="DisplaySlimView"/>) are kept current by this class.</summary>
    public IReadOnlyList<SearchModeOption> ModeOptions => _modeOptions;

    /// <summary>How many commits the back stack holds -- what <see cref="RewindStack"/> pops. Exposed for tests of the
    /// stack rule (fork :393-398: a commit replaces the top entry when the top has the same text or was a
    /// <see cref="SearchMode.CurrentTab"/> entry, so as-you-type searching never piles up one entry per keystroke).</summary>
    public int BackStackDepth => _backStack.Count;

    /// <summary>Fork :338-342, called by the view when the search box gets focus (plan §37.2 item 1): shows the panel and
    /// resets the mode to <see cref="SearchMode.CurrentTab"/>. The fork also refreshed its saved-search candidates
    /// here; those came from Twitter's saved-search API, which has no Nostr counterpart, and are not ported.</summary>
    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            return;
        }

        IsOpen = true;
        SearchMode = SearchMode.CurrentTab;
    }

    /// <summary>Fork :344-349: tears everything down (<see cref="CloseCore"/>) and hands focus back to the timeline
    /// (plan §37.2 item 8). A no-op while closed.</summary>
    public void Close()
    {
        if (!IsOpen)
        {
            return;
        }

        CloseCore();
        FocusRequested?.Invoke(SearchFocusTarget.Timeline);
    }

    /// <summary>Fork :375-384 (plan §37.2 item 6): commit if the text differs from the last commit or nothing is showing
    /// yet -- and, in query mode, only if the query compiles -- then move focus to the results. A result that is
    /// already showing for this exact text is not rebuilt; Enter then just moves focus.</summary>
    public void OnEnterKeyDown()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsOpen)
        {
            Open();
        }

        if ((_previousCommit != Text || !IsSearchResultAvailable) && (!IsQueryMode || !HasError))
        {
            CommitSearch();
        }

        if (SearchResult is not null)
        {
            FocusRequested?.Invoke(SearchFocusTarget.Results);
        }
    }

    /// <summary>Fork :463-478 (plan §37.2 item 7): drop the current commit and go back to the one before it, re-running
    /// it in its own mode; an empty stack closes the panel. Focus returns to the search box so the restored text can
    /// be edited (the fork's result view asked for the same through <c>CloseResults</c>).</summary>
    public void RewindStack()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_backStack.Count > 0)
        {
            _backStack.Pop();
        }

        if (_backStack.Count == 0)
        {
            Close();
            return;
        }

        var (text, mode) = _backStack.Peek();
        Text = text;
        if (SearchMode == mode)
        {
            CommitSearch();
        }
        else
        {
            SearchMode = mode;
        }

        FocusRequested?.Invoke(SearchFocusTarget.SearchBox);
    }

    /// <summary>Fork :450-458: clear the result area but keep the text and the stack, and put the caret back in the box.</summary>
    public void CloseResults()
    {
        IsSearchResultAvailable = false;
        SearchResult = null;
        UnavailableText = null;
        FocusRequested?.Invoke(SearchFocusTarget.SearchBox);
    }

    /// <summary>Fork <c>SearchResultViewModel.PinToTab</c>, redirected to close the flip rather than the fork's
    /// <see cref="RewindStack"/> (plan §41's brief: pin is a destination, not one more step back through the
    /// history). Plan §37.2 item 11 / §41: a no-op while nothing is committed -- an L7c mode and a query-mode error
    /// both never reach here, because <see cref="SearchResult"/> is only non-null once <see cref="CommitSearch"/>
    /// actually built one (see its branches; neither case does). A full column or an unparseable current-tab query
    /// report through <see cref="PinError"/> instead of throwing or silently doing nothing.</summary>
    public void PinToTab()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SearchResult is not { } result)
        {
            return;
        }

        var column = _currentColumn();
        if (column is null)
        {
            PinError = "No column is selected to pin into.";
            return;
        }

        if (!column.CanAddTab)
        {
            PinError = $"This column already holds {AppState.MaxTabsPerColumn} tabs, the most it can carry -- close one before pinning.";
            return;
        }

        string kql;
        try
        {
            kql = BuildPinnedQuery(result.Query);
        }
        catch (NightlyDawnException ex)
        {
            PinError = $"Could not pin: {ex.Message}";
            return;
        }

        var tab = column.AddTab();
        tab.Query = kql;
        tab.Subscribe();
        PinError = null;
        Close();
    }

    /// <summary>The query-mode text verbatim past its <c>?</c> (plan §41 item ③: it already names its own
    /// <c>from</c>, so there is nothing of the current tab's query to combine it with), or, in
    /// <see cref="SearchMode.CurrentTab"/> mode, the current tab's own query ANDed with the keyword matcher
    /// rebuilt from <paramref name="committedText"/> via <see cref="KeywordMatcher.Parse"/> and
    /// <see cref="KeywordMatcher.ToFilterNode"/> -- the same OR rule <see cref="CommitSearch"/> itself used
    /// (plan §41 item ①), so the set the pinned tab opens on is the set this result was already showing
    /// (plan §41 item ②).</summary>
    private string BuildPinnedQuery(string committedText)
    {
        if (committedText.StartsWith('?'))
        {
            return committedText[1..];
        }

        var tabQuery = _currentTab()?.Query.Trim();
        if (string.IsNullOrEmpty(tabQuery))
        {
            throw new FilterParseException("The current tab has no query of its own to combine with.");
        }

        var originalAst = _compiler.Parse(tabQuery);
        var keywordNode = KeywordMatcher.Parse(committedText).ToFilterNode();
        var combinedWhere = originalAst.Where is null ? keywordNode : new FilterAnd(originalAst.Where, keywordNode);
        return KqlWriter.Write(new FilterAst(originalAst.Source, combinedWhere));
    }

    /// <summary>Fork :168-176: next mode in enum order, wrapping, skipping <see cref="SearchMode.UserScreenName"/>
    /// unless the text qualifies.</summary>
    public void SetNextSearchOption()
    {
        var next = SearchMode == SearchMode.UserScreenName ? SearchMode.CurrentTab : (SearchMode)((int)SearchMode + 1);
        if (next == SearchMode.UserScreenName && !CanBeUserScreenName)
        {
            next = SearchMode.CurrentTab;
        }

        SearchMode = next;
    }

    /// <summary>Fork :178-186: previous mode in enum order, wrapping, skipping <see cref="SearchMode.UserScreenName"/>
    /// unless the text qualifies (so from <see cref="SearchMode.CurrentTab"/> it goes to <see cref="SearchMode.UserWeb"/>).</summary>
    public void SetPreviousSearchOption()
    {
        var previous = SearchMode == SearchMode.CurrentTab ? SearchMode.UserScreenName : (SearchMode)((int)SearchMode - 1);
        if (previous == SearchMode.UserScreenName && !CanBeUserScreenName)
        {
            previous = SearchMode.UserWeb;
        }

        SearchMode = previous;
    }

    /// <summary>Fork :162-165 (plan §37.2 item 9). The view reports the result area's width after every layout pass.</summary>
    public void NotifyResultWidthChanged(double width) => DisplaySlimView = width < SlimWidthThreshold;

    /// <summary>The fork's <c>OnTextChanged</c> (:272-336), synchronous. The <c>?</c> branch compiles and reports;
    /// the keyword branch commits at once in <see cref="SearchMode.CurrentTab"/> only, and in every other mode waits
    /// for Enter or a mode change (plan §37.2 item 4).</summary>
    private void OnTextChanged(string value)
    {
        if (!IsOpen && value.Length > 0)
        {
            Open();
        }

        if (value.StartsWith('?'))
        {
            IsQueryMode = true;
            IsSearchOptionAvailable = false;
            IsSearchResultAvailable = false;
            CanBeUserScreenName = false;
            _compiledQuery = null;
            if (value.Length == 1)
            {
                ErrorText = QueryEmptyText;
                return;
            }

            ErrorText = TryCompileQuery(value[1..], out _compiledQuery);
            RaisePropertyChanged(nameof(QueryStatusText));
            return;
        }

        IsQueryMode = false;
        ErrorText = null;
        _compiledQuery = null;
        RaisePropertyChanged(nameof(QueryStatusText));
        if (value.Length == 0)
        {
            IsSearchResultAvailable = false;
            IsSearchOptionAvailable = false;
            CanBeUserScreenName = false;
            return;
        }

        IsSearchOptionAvailable = true;
        CanBeUserScreenName = UserIdentifierRegex.IsMatch(value.Trim());
        if (SearchMode == SearchMode.CurrentTab)
        {
            CommitSearch();
        }
    }

    /// <summary>Compiles <paramref name="kql"/> the way a tab query is compiled, with no account and no follow list:
    /// the panel searches what the tab already holds, so there is nothing those would be resolved for. Returns the
    /// message to show, or null when the query compiles; <paramref name="predicate"/> is then the local half of the
    /// compiled filter (plan §7.2: the whole query, re-evaluated locally).</summary>
    private string? TryCompileQuery(string kql, out Func<Note, bool>? predicate)
    {
        predicate = null;
        try
        {
            var compiled = _compiler.Compile(_compiler.Parse(kql));
            predicate = compiled.LocalPredicate ?? (_ => true);
            return null;
        }
        catch (UnsupportedFilterFieldException ex) when (ex.Field is "from home" or "from mentions")
        {
            return $"{ex.Field} needs the signed-in account's follow list, which the search panel does not resolve; search by keyword or use another source.";
        }
        catch (NightlyDawnException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Fork :390-448. Records the commit on the back stack (collapsing onto the top entry when it has the
    /// same text or was a <see cref="SearchMode.CurrentTab"/> one), then builds the result for the current text and
    /// mode. Blank text commits nothing but still pushes, exactly as the fork did -- so a mode change on an empty box
    /// is one Esc away from closing, not zero.</summary>
    private void CommitSearch()
    {
        _previousCommit = Text;
        PinError = null; // A new commit is a new result; a stale "column is full" from the last one would outlive it.
        if (_backStack.Count > 0)
        {
            var top = _backStack.Peek();
            if (top.Text == Text || top.Mode == SearchMode.CurrentTab)
            {
                _backStack.Pop();
            }
        }

        _backStack.Push((Text, SearchMode));
        RaisePropertyChanged(nameof(BackStackDepth));

        if (string.IsNullOrWhiteSpace(Text))
        {
            IsSearchResultAvailable = false;
            SearchResult = null;
            UnavailableText = null;
            return;
        }

        SearchResult = null;
        UnavailableText = null;

        if (IsQueryMode)
        {
            if (_compiledQuery is null)
            {
                IsSearchResultAvailable = false; // the query has an error; OnEnterKeyDown never gets here, a mode change cannot (the list is hidden).
                return;
            }

            ShowResult(_compiledQuery);
        }
        else if (SearchMode == SearchMode.CurrentTab)
        {
            var matcher = KeywordMatcher.Parse(Text);
            if (matcher.IsEmpty)
            {
                // Only exclusions: the fork's Any() over no positive words matched nothing. Say so rather than show
                // an empty list that looks like "no note contains this".
                UnavailableText = "Add a word to search for; -word only excludes.";
            }
            else
            {
                ShowResult(note => matcher.IsMatch(note.Content));
            }
        }
        else
        {
            var option = _modeOptions.First(o => o.Mode == SearchMode);
            UnavailableText = $"{option.Label} search is not implemented yet (L7c). Only \"This tab\" searches in this build.";
        }

        IsSearchResultAvailable = true;
    }

    private void ShowResult(Func<Note, bool> predicate)
    {
        var tab = _currentTab();
        if (tab is null)
        {
            UnavailableText = "No tab is selected to search in.";
            return;
        }

        SearchResult = new SearchResultViewModel(tab, predicate, Text);
    }

    /// <summary>Fork :351-360: empty the box, drop the result and the stack, hide the panel. <see cref="Text"/>'s
    /// setter runs <see cref="OnTextChanged"/> for the empty string, which clears the query-mode flags too.</summary>
    private void CloseCore()
    {
        Text = string.Empty;
        _previousCommit = null;
        IsSearchResultAvailable = false;
        SearchResult = null;
        UnavailableText = null;
        PinError = null;
        IsOpen = false;
        _backStack.Clear();
        RaisePropertyChanged(nameof(BackStackDepth));
    }

    private void RefreshModeOptions()
    {
        foreach (var option in _modeOptions)
        {
            option.IsSelected = option.Mode == _searchMode;
            option.IsVisible = option.Mode != SearchMode.UserScreenName || _canBeUserScreenName;
            option.ShowDescription = !_displaySlimView;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SearchResult = null;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One entry of <see cref="SearchFlipViewModel.ModeOptions"/>: a <see cref="SearchMode"/> with the label and
/// description the panel shows for it. The three flags are written by <see cref="SearchFlipViewModel"/> only.</summary>
public sealed class SearchModeOption(SearchMode mode, string label, string description) : INotifyPropertyChanged
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _showDescription = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchMode Mode { get; } = mode;

    public string Label { get; } = label;

    public string Description { get; } = description;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetField(ref _isVisible, value);
    }

    public bool ShowDescription
    {
        get => _showDescription;
        internal set => SetField(ref _showDescription, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
