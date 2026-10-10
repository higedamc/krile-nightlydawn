namespace NightlyDawn.Core;

/// <summary>
/// Caches kind:0 profiles so a timeline column can show author display names without a REQ per row.
/// <see cref="INostrBackend.FetchProfileEventAsync"/> is one author per REQ and waits for EOSE from every
/// relay (up to its implementation's fetch timeout) -- a 200-author column must not issue 200 of those, so
/// batching is part of the contract, not an implementation detail (plan §1 item 5).
///
/// <para>Declaration only (0d leaf brief): no implementation here -- that is L2, after which rows can show a
/// display name instead of the hex-prefix fallback they use today.</para>
/// </summary>
public interface IProfileStore
{
    /// <summary>The cached profile for <paramref name="pubkey"/>, or null if nothing is cached -- either
    /// because it has never been asked for, or because <see cref="PrefetchAsync"/> already asked and found no
    /// kind:0 (the negative-cache case; see that method's remarks). Never issues a network request itself:
    /// callers prefetch first and read this synchronously once rendering.</summary>
    Profile? TryGet(string pubkey);

    /// <summary>
    /// Fetches and caches a profile for every pubkey in <paramref name="pubkeys"/> that is not already
    /// cached, in one batched query -- implementations should drive this through
    /// <see cref="INostrBackend.FetchAsync"/> with <c>Authors: pubkeys, Kinds: [0]</c>, not one
    /// <see cref="INostrBackend.FetchProfileEventAsync"/> call per pubkey.
    /// <list type="bullet">
    /// <item><description>When more than one kind:0 for the same author comes back, the one with the newest
    /// <see cref="Profile.UpdatedAt"/> wins (NIP-01 replaceable-event semantics); an already-cached profile is
    /// only replaced by a strictly newer one, never by a tie or an older event.</description></item>
    /// <item><description>A pubkey with no kind:0 anywhere in the results is still recorded as "asked, found
    /// nothing" (a negative cache), so a repeated <see cref="PrefetchAsync"/> call for the same pubkey does
    /// not re-issue a query for it within the implementation's cache lifetime.
    /// <see cref="TryGet"/> returns null either way -- callers that only render never need to tell
    /// "unresolved" from "confirmed absent" apart, only the implementation's own re-fetch decision
    /// does.</description></item>
    /// </list>
    /// </summary>
    Task PrefetchAsync(IReadOnlyCollection<string> pubkeys, CancellationToken cancellationToken = default);
}
