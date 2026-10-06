using System.Threading;

namespace NightlyDawn.Core;

/// <summary>Relay pool: connect, subscribe, publish, and fetch profiles/relay lists. Implemented in phase 1a by a Nostr.Sdk adapter (NightlyDawn.Nostr). Does not sign — callers pass already-signed events from <see cref="IKeyStore"/>.
/// <para>Relays are adversarial input. Implementations MUST verify every incoming event before yielding or returning it — <c>id</c> equal to the SHA-256 of the NIP-01 serialization, and <c>sig</c> valid under BIP-340 for <c>pubkey</c> — and drop events that fail rather than surfacing them. Callers may assume any event this interface hands them, directly or via a derived type like <see cref="RelayListEntry"/>, is authenticated (B13).</para>
/// </summary>
public interface INostrBackend
{
    Task ConnectAsync(IReadOnlyCollection<RelayUrl> relayUrls, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Live subscription. Prefer <see cref="FetchAsync"/> for a one-shot query that needs to know when stored events are exhausted. Only verified events are yielded (see interface summary).</summary>
    IAsyncEnumerable<SubscriptionMessage> SubscribeAsync(NostrFilter filter, CancellationToken cancellationToken = default);

    /// <summary>One-shot query: waits for EOSE from every currently-connected relay, not just the first, before returning (B6, meiso Q1). Only verified events are returned (see interface summary).</summary>
    Task<IReadOnlyList<NostrEvent>> FetchAsync(NostrFilter filter, CancellationToken cancellationToken = default);

    /// <exception cref="EventPublishException">No relay accepted the event.</exception>
    Task<PublishResult> PublishAsync(NostrEvent signedEvent, CancellationToken cancellationToken = default);

    /// <summary>Returns a verified kind:0 event, or null (see interface summary).</summary>
    Task<NostrEvent?> FetchProfileEventAsync(string pubkey, CancellationToken cancellationToken = default);

    /// <summary>Returns a relay list derived from a verified kind:10002 event, or null (see interface summary).</summary>
    Task<RelayListEntry?> FetchRelayListAsync(string pubkey, CancellationToken cancellationToken = default);
}

/// <summary>Signing and key lifecycle. Never exposes raw key material to callers except through <see cref="ExportLocalKeyAsync"/>'s NIP-49 ciphertext — generation, import, signing, and encryption all stay inside this store. Implemented in phase 1b: NIP-46 remote signer and a NIP-49-encrypted local key, with any passphrase held only for the duration of a single call (plan §5 D4).</summary>
public interface IKeyStore
{
    Task<SignerDescriptor> GetActiveSignerAsync(CancellationToken cancellationToken = default);

    Task<NostrEvent> SignEventAsync(UnsignedNostrEvent unsignedEvent, CancellationToken cancellationToken = default);

    /// <summary>NIP-44 encrypt to <paramref name="peerPubkey"/> using the active signer (B3 — needed by NIP-17 DMs and self-encrypted storage).</summary>
    Task<string> Nip44EncryptAsync(string peerPubkey, string plaintext, CancellationToken cancellationToken = default);

    /// <summary>NIP-44 decrypt a payload from <paramref name="peerPubkey"/> using the active signer.</summary>
    Task<string> Nip44DecryptAsync(string peerPubkey, string ciphertext, CancellationToken cancellationToken = default);

    /// <summary>Generates a <c>nostrconnect://</c> URI for the app to display (QR/text); <see cref="RemoteSignerPairing.Completion"/> resolves once the signer accepts (B4).</summary>
    Task<RemoteSignerPairing> BeginRemoteSignerPairingAsync(IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Connects to a signer the user already has a <c>bunker://</c> URI for; <paramref name="progress"/> reports an <c>auth_url</c> if the bunker sends one (B4).</summary>
    Task<SignerDescriptor> ConnectBunkerAsync(string bunkerUri, IProgress<RemoteSignerPrompt>? progress = null, CancellationToken cancellationToken = default);

    /// <param name="nip49EncryptedKey">A NIP-49 <c>ncryptsec1...</c> encrypted key.</param>
    /// <param name="passphrase">Used only to decrypt; never persisted (S2: scrubbable buffer rather than an immutable <see cref="string"/>).</param>
    Task<SignerDescriptor> ImportLocalKeyAsync(string nip49EncryptedKey, ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default);

    /// <summary>Generates a new local key — CSPRNG-sourced, with the secp256k1 valid-range check the implementation's responsibility — and stores it NIP-49-encrypted. Never returns the raw secret key; only <see cref="SignerDescriptor"/> (pubkey + metadata) (B11).</summary>
    /// <param name="passphrase">Encrypts the new key at rest as NIP-49 <c>ncryptsec1</c>; the passphrase itself is never persisted.</param>
    Task<SignerDescriptor> GenerateLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default);

    /// <summary>Exports the active local key as NIP-49 <c>ncryptsec1</c> — the only way a key ever leaves this store. There is no plaintext-nsec export (B11).</summary>
    /// <param name="passphrase">Re-encrypts the exported key under this passphrase (not necessarily the same one used at generation/import).</param>
    /// <exception cref="SignerUnavailableException">The active signer is remote (NIP-46); this store never holds its raw key material to export.</exception>
    Task<string> ExportLocalKeyAsync(ReadOnlyMemory<char> passphrase, CancellationToken cancellationToken = default);

    /// <summary>Clears this store's own key state. Callers are responsible for clearing any other <c>nostr.*</c> app state.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>A live feed behind a <see cref="Timeline"/> column. Implemented in phase 1 by composing <see cref="INostrBackend"/> and <see cref="IFilterCompiler"/>.</summary>
public interface ITimelineSource
{
    IAsyncEnumerable<TimelineUpdate> StreamAsync(CancellationToken cancellationToken = default);

    /// <param name="beforeCreatedAt">Page strictly older than this timestamp — Nostr pages by <c>until</c>, and note ids can be evicted from relay storage (B9).</param>
    Task<IReadOnlyList<Note>> LoadOlderAsync(long beforeCreatedAt, int limit, CancellationToken cancellationToken = default);
}

/// <summary>Decouples the App layer from the Nostr project: phase 1e asks for a source by <see cref="Timeline"/> + <see cref="Account"/> and never references NightlyDawn.Nostr directly (B9).</summary>
public interface ITimelineSourceFactory
{
    ITimelineSource Create(Timeline timeline, Account account);
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
    /// <param name="nostrEvent">A kind:1/6/16 event.</param>
    /// <exception cref="EventMappingException">The event is malformed. Callers should skip and log, not crash the subscription (B10).</exception>
    Note ToNote(NostrEvent nostrEvent);

    /// <param name="nostrEvent">A kind:0 event.</param>
    /// <exception cref="EventMappingException">The event is malformed. Callers should skip and log, not crash the subscription (B10).</exception>
    Profile ToProfile(NostrEvent nostrEvent);

    /// <param name="nostrEvent">A kind:10002 event (NIP-65).</param>
    /// <exception cref="EventMappingException">The event is malformed. Callers should skip and log, not crash the subscription (B10).</exception>
    RelayListEntry ToRelayList(NostrEvent nostrEvent);
}
