namespace NightlyDawn.Nostr.Tests.Fakes;

/// <summary>Deterministic <see cref="TimeProvider"/> for publisher tests (L3a): always returns the
/// constructor's instant, so <c>created_at</c> assertions do not race the real clock.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
