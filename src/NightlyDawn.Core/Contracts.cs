using System.Threading;

namespace NightlyDawn.Core;

/// <summary>Relay pool: connect, subscribe, publish, and fetch profiles/relay lists. Implemented in phase 1a by a Nostr.Sdk adapter (NightlyDawn.Nostr). Does not sign — callers pass already-signed events from <see cref="IKeyStore"/>.</summary>
public interface INostrBackend
{
    Task ConnectAsync(IReadOnlyCollection<string> relayUrls, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<NostrEvent> SubscribeAsync(NostrFilter filter, CancellationToken cancellationToken = default);

    Task PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default);

    Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default);

    Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default);
}

/// <summary>Signing. Never exposes raw key material to callers — only signs on request. Implemented in phase 1b: NIP-46 remote signer and a NIP-49-encrypted local key, with the decryption passphrase held only for the duration of a sign/import call (plan §5 D4).</summary>
public interface IKeyStore
{
    Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default);

    Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default);

    /// <param name="bunkerOrConnectUri">A <c>bunker://</c> or <c>nostrconnect://</c> URI (NIP-46).</param>
    Task<SignerDescriptor> ConnectRemoteSignerAsync(string bunkerOrConnectUri, CancellationToken cancellationToken = default);

    /// <param name="nip49EncryptedKey">A NIP-49 <c>ncryptsec1...</c> encrypted key.</param>
    /// <param name="passphrase">Used only to decrypt; never persisted.</param>
    Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, string passphrase, CancellationToken cancellationToken = default);

    /// <summary>Clears this store's own key state. Callers are responsible for clearing any other <c>nostr.*</c> app state.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>A live or paged feed behind a <see cref="Timeline"/> column. Implemented in phase 1 by composing <see cref="INostrBackend"/> and <see cref="IFilterCompiler"/>.</summary>
public interface ITimelineSource
{
    IAsyncEnumerable<Note> StreamAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Note>> LoadOlderAsync(string beforeNoteId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>KQL parsing and relay-filter compilation. Implemented in phase 1c (NightlyDawn.Filters, ported from StarryEyes/Filters).</summary>
public interface IFilterCompiler
{
    /// <exception cref="FilterParseException">The input is not valid KQL.</exception>
    FilterAst Parse(string kql);

    CompiledFilter Compile(FilterAst ast);
}

/// <summary>Maps wire-level <see cref="NostrEvent"/>s onto domain types. Implemented in phase 1a alongside the Nostr.Sdk adapter.</summary>
public interface IEventMapper
{
    Note ToNote(NostrEvent nostrEvent);

    /// <param name="nostrEvent">A kind:0 event.</param>
    Profile ToProfile(NostrEvent nostrEvent);

    /// <param name="nostrEvent">A kind:10002 event (NIP-65).</param>
    RelayListEntry ToRelayList(NostrEvent nostrEvent);
}
