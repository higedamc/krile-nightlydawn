namespace NightlyDawn.Core;

// Wire-level event shapes (NIP-01). Tags are the raw array-of-arrays the
// protocol uses, e.g. ["e", "<id>", "<relay>", "reply"].

public sealed record UnsignedNostrEvent(
    string Pubkey,
    long CreatedAt,
    int Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content);

public sealed record NostrEvent(
    string Id,
    string Pubkey,
    long CreatedAt,
    int Kind,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string Content,
    string Sig);

/// <summary>A NIP-01 REQ filter. Null members are omitted from the wire filter.</summary>
public sealed record NostrFilter(
    IReadOnlyList<string>? Ids = null,
    IReadOnlyList<string>? Authors = null,
    IReadOnlyList<int>? Kinds = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? TagFilters = null,
    long? Since = null,
    long? Until = null,
    int? Limit = null,
    string? Search = null);

public enum SignerKind
{
    Nip07,
    Nip46,
    Nip49Local,
}

public sealed record SignerDescriptor(string Pubkey, SignerKind Kind, string? DisplayLabel = null);

// Domain entities (plan §5).

public sealed record Account(string Pubkey, SignerDescriptor Signer, RelayListEntry? RelayList = null);

public sealed record RelayListEntry(
    string Pubkey,
    IReadOnlyList<string> ReadRelays,
    IReadOnlyList<string> WriteRelays);

public sealed record Profile(
    string Pubkey,
    string? Name = null,
    string? DisplayName = null,
    string? About = null,
    string? Picture = null,
    string? Banner = null,
    string? Nip05 = null,
    string? Lud16 = null,
    long? UpdatedAt = null);

public enum NoteKind
{
    Text = 1,
    Repost = 6,
    GenericRepost = 16,
}

/// <summary>Domain-mapped kind:1/6/16 event (plan §4: Status, Retweet/Quote, Reply).</summary>
public sealed record Note(
    string Id,
    string AuthorPubkey,
    long CreatedAt,
    NoteKind Kind,
    string Content,
    string? RootId = null,
    string? ReplyId = null,
    string? QuotedNoteId = null,
    IReadOnlyList<string>? MentionedPubkeys = null);

public sealed record Timeline(string Id, string Title, FilterAst Filter);

public sealed record Tab(string Id, string Title, IReadOnlyList<Timeline> Columns);

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed record Settings(
    AppTheme Theme = AppTheme.System,
    string Locale = "en",
    IReadOnlyDictionary<string, string>? Extra = null);
