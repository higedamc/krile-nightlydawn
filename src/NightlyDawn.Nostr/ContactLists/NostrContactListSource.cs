using NightlyDawn.Core;
using NightlyDawn.Nostr.Wire;

namespace NightlyDawn.Nostr.ContactLists;

/// <summary>
/// <see cref="IContactListSource"/> backed by <see cref="INostrBackend.FetchAsync"/> (NIP-02, kind:3). Mirrors
/// <see cref="NightlyDawn.Nostr.Relay.NostrBackend.FetchRelayListAsync"/>'s "newest wins across every relay that
/// answered" shape rather than adding a dedicated backend method, since this is the only caller.
/// <para>No kind:3 event anywhere, and a kind:3 event with no "p" tags, both resolve to an empty (never null)
/// list — "follows nobody yet" and "never published a contact list" are indistinguishable to a relay and are
/// treated the same here; the caller doc (<see cref="IContactListSource.FetchContactListAsync"/>) makes this
/// explicit so "from home" compiling to a timeline that matches nothing is not mistaken for a bug.</para>
/// <para>Relays are adversarial input (B13): a "p" tag whose value is not a 64-character lowercase hex pubkey is
/// dropped rather than propagated, same as <see cref="NightlyDawn.Nostr.Mapping.EventMapper"/> does for the "p"
/// tags it reads off note events.</para>
/// </summary>
public sealed class NostrContactListSource(INostrBackend backend) : IContactListSource
{
    private const int ContactListKind = 3;

    public async Task<IReadOnlyList<string>> FetchContactListAsync(string pubkey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pubkey);

        var events = await backend.FetchAsync(new NostrFilter(Authors: [pubkey], Kinds: [ContactListKind], Limit: 1), cancellationToken).ConfigureAwait(false);
        var newest = events.Where(e => e.Pubkey == pubkey).MaxBy(e => e.CreatedAt);
        if (newest is null)
        {
            return [];
        }

        var follows = new List<string>();
        foreach (var tag in newest.Tags)
        {
            if (tag.Count >= 2 && tag[0] == "p" && EventVerifier.IsLowerHex(tag[1], 64) && !follows.Contains(tag[1], StringComparer.Ordinal))
            {
                follows.Add(tag[1]);
            }
        }

        return follows;
    }
}
