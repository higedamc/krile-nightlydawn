using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>Test double for <see cref="IContactListSource"/>: returns a fixed, pre-set follow list (empty by
/// default — "follows nobody") with no I/O, so tests that do not care about <c>from home</c>'s kind:3 fetch can
/// still exercise the real <c>NostrFilterQueryCompiler</c>/<c>FilterCompiler</c> pair end to end.</summary>
public sealed class FakeContactListSource : IContactListSource
{
    public IReadOnlyList<string> Follows { get; set; } = [];

    public int FetchCalls { get; private set; }

    public Task<IReadOnlyList<string>> FetchContactListAsync(string pubkey, CancellationToken cancellationToken = default)
    {
        FetchCalls++;
        return Task.FromResult(Follows);
    }
}
