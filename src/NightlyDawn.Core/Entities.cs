namespace NightlyDawn.Core;

// Wire-level event shapes (NIP-01). Tags are the raw array-of-arrays the
// protocol uses, e.g. ["e", "<id>", "<relay>", "reply"].

/// <param name="Pubkey">Null until a signer fills it in (S1).</param>
public sealed record UnsignedNostrEvent(
    string? Pubkey,
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

/// <summary>A relay URL restricted to <c>wss://</c> unless the caller opts into <c>ws://</c> for local development (plan §5 security requirement, S3). <see cref="Parse"/> is the only way to construct one — this is a <c>sealed record</c> (reference type), not a struct, so there is no public parameterless constructor to bypass validation via <c>default</c>/<c>new RelayUrl[n]</c> (B12).</summary>
public sealed record RelayUrl
{
    public string Value { get; }

    private RelayUrl(string value) => Value = value;

    public static RelayUrl Parse(string value, bool allowInsecureForDevelopment = false)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Relay URL is not a valid absolute URI: {value}", nameof(value));
        }

        if (string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return new RelayUrl(value);
        }

        if (allowInsecureForDevelopment && string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase))
        {
            return new RelayUrl(value);
        }

        throw new ArgumentException(
            $"Relay URL must use wss:// (ws:// only when allowInsecureForDevelopment is set): {value}",
            nameof(value));
    }

    public override string ToString() => Value;
}

public enum SignerKind
{
    Nip46,
    Nip49Local,
}

public sealed record SignerDescriptor(string Pubkey, SignerKind Kind, string? DisplayLabel = null);

/// <summary>Progress reported while pairing a remote signer, e.g. a bunker's <c>auth_url</c> the user must open (B4).</summary>
public sealed record RemoteSignerPrompt(string Message, string? AuthUrl = null);

/// <summary><see cref="NostrConnectUri"/> is what the app displays (QR/text) for the user's signer to scan or paste; <see cref="Completion"/> resolves once the signer accepts (B4).</summary>
public sealed record RemoteSignerPairing(string NostrConnectUri, Task<SignerDescriptor> Completion);

public sealed record RelayPublishOutcome(RelayUrl RelayUrl, bool Accepted, string? Reason = null);

/// <summary>Per-relay publish outcome (B5: 0-of-N accepted must not read as success).</summary>
public sealed record PublishResult(IReadOnlyList<RelayPublishOutcome> Outcomes)
{
    public bool AnyAccepted => Outcomes.Any(o => o.Accepted);
}

public abstract record SubscriptionMessage;

public sealed record EventReceived(NostrEvent Event, RelayUrl RelayUrl) : SubscriptionMessage;

public sealed record EndOfStoredEvents(RelayUrl RelayUrl) : SubscriptionMessage;

public sealed record SubscriptionClosed(string? Reason = null) : SubscriptionMessage;

// Domain entities (plan §5).

public sealed record Account(string Pubkey, SignerDescriptor Signer, RelayListEntry? RelayList = null);

public sealed record RelayListEntry(
    string Pubkey,
    IReadOnlyList<RelayUrl> ReadRelays,
    IReadOnlyList<RelayUrl> WriteRelays);

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

/// <summary>Domain-mapped kind:1/6/16 event (plan §4: Status, Retweet/Quote, Reply). <see cref="Tags"/> keeps the raw tags so KQL's <c>tags.t</c>/<c>relay</c> fields (plan §4) stay expressible (B7).</summary>
public sealed record Note(
    string Id,
    string AuthorPubkey,
    long CreatedAt,
    NoteKind Kind,
    string Content,
    IReadOnlyList<IReadOnlyList<string>> Tags,
    string? RootId = null,
    string? ReplyId = null,
    string? QuotedNoteId = null,
    string? RepostedNoteId = null,
    IReadOnlyList<string>? MentionedPubkeys = null,
    IReadOnlyList<string>? Hashtags = null,
    IReadOnlyList<string>? SeenOnRelays = null);

public abstract record TimelineUpdate;

public sealed record NoteArrived(Note Note) : TimelineUpdate;

/// <summary>Signals the column's initial backlog is loaded, so the UI can leave its "loading" state (B9).</summary>
public sealed record InitialLoadComplete : TimelineUpdate;

/// <summary><see cref="KqlQuery"/> is persisted as raw text and compiled at load time, so storage isn't tied to the AST's shape as the grammar evolves (B8).</summary>
public sealed record Timeline(string Id, string Title, string KqlQuery);

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
