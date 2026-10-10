namespace NightlyDawn.Core;

/// <summary>
/// Resolves "from home"'s missing dependency: the pubkeys an account follows (NIP-02, kind:3). `from home` is
/// already advertised in the UI's query placeholder, but nothing fetches a contact list today
/// (<c>NostrTimelineSourceFactory.Create</c> ignores the <see cref="Account"/> it is given) -- this is the
/// contract a later leaf wires up to close that gap (plan §1 item A).
///
/// <para>Declared on its own interface rather than as a new method on <see cref="INostrBackend"/> so this
/// file stays new (0d leaf brief: no edits to <c>Contracts.cs</c>, to stay file-orthogonal with the leaves
/// that start right after 0d lands). An implementation can still live on the same class that implements
/// <see cref="INostrBackend"/> -- <see cref="INostrBackend.FetchAsync"/> already has everything a kind:3
/// query needs; what's missing is just the mapping from the event's <c>p</c> tags to a pubkey list.</para>
///
/// <para>Declaration only (0d leaf brief): no implementation here.</para>
/// </summary>
public interface IContactListSource
{
    /// <summary>The pubkeys <paramref name="pubkey"/> follows, read from their newest kind:3 event. Empty
    /// (never null) when the account has no contact list yet.</summary>
    Task<IReadOnlyList<string>> FetchContactListAsync(string pubkey, CancellationToken cancellationToken = default);
}
