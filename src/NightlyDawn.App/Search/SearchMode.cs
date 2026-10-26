namespace NightlyDawn.App.Search;

/// <summary>
/// Where a search looks, in the fork's order (StarryEyes <c>Models/SearchFlipModel.cs</c>'s <c>SearchMode</c>,
/// plan §37.2 item 2): <see cref="SearchFlipViewModel.SetNextSearchOption"/>/<see cref="SearchFlipViewModel.SetPreviousSearchOption"/>
/// cycle through these by their integer value, so the order here is the order the user sees and is not
/// cosmetic. Only <see cref="CurrentTab"/> searches in L7a; the other four are selectable so the fork's state
/// machine (mode-cycling, the back stack's per-mode entries) is complete, and each reports itself as not
/// implemented when committed (plan §37.5: L7c owns their contents; a mode that could be chosen and then did
/// nothing would be the dead UI L4c ④ ruled out). Plan §37.4 has the Nostr mapping for each.
/// </summary>
public enum SearchMode
{
    /// <summary>Filter the notes the selected tab is showing right now (fork: "From the tab"). Works offline, so it
    /// is the only mode L7a implements and the only one that searches as you type (plan §37.2 item 4).</summary>
    CurrentTab,

    /// <summary>Everything any tab currently holds (fork: the local database). L7c; plan §37.4 says a persistent
    /// event cache does not exist yet, so the interim scope would be the union of all tabs, labelled as such.</summary>
    Local,

    /// <summary>Ask relays (fork: Twitter's search API; here NIP-50 <c>search</c>, only to relays that advertise
    /// NIP 50 in their NIP-11 document). L7c.</summary>
    Web,

    /// <summary>Search people (fork: Twitter user search; here NIP-50 against kind:0). L7c.</summary>
    UserWeb,

    /// <summary>Open one person's page from an identifier (fork: <c>@screen_name</c>; here an <c>npub1…</c>,
    /// <c>nprofile1…</c>, 64-hex pubkey or NIP-05 <c>name@domain</c>). Offered in the cycle only while the text
    /// looks like one of those (<see cref="SearchFlipViewModel.CanBeUserScreenName"/>). L7c.</summary>
    UserScreenName,
}
