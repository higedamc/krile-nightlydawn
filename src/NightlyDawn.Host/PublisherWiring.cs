using NightlyDawn.Core;
using NightlyDawn.Nostr.Timelines;

namespace NightlyDawn.Host;

/// <summary>
/// The one decision in wiring the publisher: with no key store there is no publisher (L3c). Lives here, as its
/// own function, so it can be asserted on — <see cref="Program.Main"/> has no seam a test can reach.
/// </summary>
internal static class PublisherWiring
{
    /// <summary>
    /// Returns a publisher over <paramref name="timelineSourceFactory"/>'s backend, or <c>null</c> when
    /// <paramref name="keyStore"/> is null.
    ///
    /// <para>Null is a real state, not a defensive branch: the host leaves
    /// <c>AppServices.KeyStore</c> null when it could not create a data directory, and the App layer already
    /// treats a null publisher as "publishing unavailable" rather than crashing (L3b reads it through a
    /// <c>Func&lt;INotePublisher?&gt;</c>). Constructing a publisher that cannot sign would replace that
    /// up-front state with a signing failure on every attempt, after the user typed.</para>
    /// </summary>
    internal static INotePublisher? Create(NostrTimelineSourceFactory timelineSourceFactory, IKeyStore? keyStore)
    {
        ArgumentNullException.ThrowIfNull(timelineSourceFactory);
        return keyStore is null ? null : timelineSourceFactory.CreatePublisher(keyStore);
    }
}
