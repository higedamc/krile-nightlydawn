using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.Host.Tests;

/// <summary>
/// L5i: the host's KQL-to-timeline wiring. Asserted against a real query rather than a fake compiler, so this
/// fails if the composition is ever wired with something that merely satisfies <c>IFilterCompiler</c>'s type
/// without doing real work — see <see cref="TimelineSourceFactoryWiring"/>'s doc comment for the exact
/// regression this guards (L1 §12).
/// </summary>
public sealed class TimelineSourceFactoryWiringTests
{
    [Fact]
    public async Task Create_WiresRealKql_SoAnOrdinaryQueryActuallyCompiles()
    {
        await using var factory = TimelineSourceFactoryWiring.Create([RelayUrl.Parse("wss://relay.example/")]);

        // Mutating this wiring back to a factory with no real IFilterCompiler would not fail here (nothing
        // throws constructing the source) but would throw the first time the query is actually compiled —
        // which CreateAnonymous does synchronously for any non-`from home` source.
        var source = factory.CreateAnonymous(new Timeline("c", "kind:1", "from kind(\"1\") where ()"));

        Assert.NotNull(source);
    }

    [Fact]
    public void Create_RejectsNoRelays()
    {
        Assert.Throws<ArgumentException>(() => TimelineSourceFactoryWiring.Create([]));
    }
}
