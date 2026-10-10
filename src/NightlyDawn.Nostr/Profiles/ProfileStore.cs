using System.Collections.Concurrent;
using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Profiles;

/// <summary>
/// <see cref="IProfileStore"/> backed by <see cref="INostrBackend.FetchAsync"/>. See the interface doc for the
/// batching, newest-wins, and negative-cache contract this implements.
/// </summary>
public sealed class ProfileStore(INostrBackend backend, IEventMapper mapper) : IProfileStore
{
    // null = asked, no kind:0 found anywhere (negative cache). Absent key = never asked.
    private readonly ConcurrentDictionary<string, Profile?> _cache = new(StringComparer.Ordinal);

    public Profile? TryGet(string pubkey) => _cache.TryGetValue(pubkey, out var profile) ? profile : null;

    public async Task PrefetchAsync(IReadOnlyCollection<string> pubkeys, CancellationToken cancellationToken = default)
    {
        var unresolved = pubkeys.Distinct(StringComparer.Ordinal).Where(p => !_cache.ContainsKey(p)).ToList();
        if (unresolved.Count == 0)
        {
            return; // Every requested pubkey is already resolved or negative-cached: no REQ at all, not even an empty one.
        }

        // One REQ for every unresolved pubkey -- this is the batching the interface doc requires, not one
        // FetchAsync call per pubkey (which the "1 at a time" negative control catches if ever reintroduced).
        var events = await backend.FetchAsync(new NostrFilter(Authors: unresolved, Kinds: [0]), cancellationToken).ConfigureAwait(false);

        foreach (var pubkey in unresolved)
        {
            _cache.TryAdd(pubkey, null); // Default to "asked, not found"; a matching event below overwrites this.
        }

        foreach (var nostrEvent in events)
        {
            Profile profile;
            try
            {
                profile = mapper.ToProfile(nostrEvent);
            }
            catch (EventMappingException)
            {
                continue; // One malformed kind:0 in the batch must not drop the rest of it.
            }

            _cache.AddOrUpdate(
                profile.Pubkey,
                profile,
                (_, existing) => existing is null || (profile.UpdatedAt ?? long.MinValue) > (existing.UpdatedAt ?? long.MinValue)
                    ? profile
                    : existing);
        }
    }
}
