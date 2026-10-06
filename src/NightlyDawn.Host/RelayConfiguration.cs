using NightlyDawn.Core;

namespace NightlyDawn.Host;

/// <summary>
/// Which relays the first visible timeline reads from. Until relay settings exist in the UI (NIP-65 per account
/// arrives with keys), the list comes from <c>NIGHTLYDAWN_RELAYS</c> (comma-separated, <c>wss://</c> only) or the
/// defaults below. Parsing goes through <see cref="RelayUrl.Parse"/>, so <c>ws://</c> and junk are rejected here,
/// before any socket is opened.
/// </summary>
public static class RelayConfiguration
{
    public const string EnvironmentVariable = "NIGHTLYDAWN_RELAYS";

    public static readonly IReadOnlyList<string> DefaultRelays = ["wss://relay.damus.io", "wss://nos.lol"];

    /// <exception cref="ArgumentException">An entry is not a valid <c>wss://</c> relay URL.</exception>
    public static IReadOnlyList<RelayUrl> Parse(string? environmentValue)
    {
        var candidates = string.IsNullOrWhiteSpace(environmentValue)
            ? DefaultRelays
            : environmentValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var relays = new List<RelayUrl>();
        foreach (var candidate in candidates)
        {
            var relay = RelayUrl.Parse(candidate); // throws ArgumentException with the offending value
            if (!relays.Contains(relay))
            {
                relays.Add(relay);
            }
        }

        if (relays.Count == 0)
        {
            throw new ArgumentException($"{EnvironmentVariable} is set but contains no relay URLs.");
        }

        return relays;
    }

    public static IReadOnlyList<RelayUrl> FromEnvironment() => Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));
}
