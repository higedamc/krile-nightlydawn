using NightlyDawn.Core;
using NightlyDawn.Nostr.Wire;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// Minimal query language for the first visible timeline (???'s goal 3), deliberately not KQL: whitespace-separated
/// tokens, each one of
/// <list type="bullet">
/// <item><c>kind:1</c> / <c>kinds:1,6,16</c> — event kinds (default: <c>1</c>)</item>
/// <item><c>author:&lt;hex64&gt;</c> / <c>authors:a,b</c> — author pubkeys as 64-char hex (npub arrives once bech32 lives in Core)</item>
/// <item><c>#nostr</c> / <c>t:nostr</c> / <c>tags:a,b</c> — <c>#t</c> hashtag filter, lower-cased (NIP-24)</item>
/// <item><c>limit:50</c> — relay-side limit, clamped to [1, <see cref="MaxLimit"/>] (default: <see cref="DefaultLimit"/>)</item>
/// </list>
/// Everything compiles to a relay-side <see cref="NostrFilter"/>; there is never a local predicate. KQL's
/// <c>from … where …</c> is rejected with a pointer to 1c rather than half-parsed.
/// </summary>
public sealed class SimpleTimelineQueryCompiler(int defaultLimit = 50, int maxLimit = 500) : ITimelineQueryCompiler
{
    public const int DefaultKind = 1;
    private const int MaxKind = 65_535;
    private const int MaxHashtagChars = 100;
    private const int MaxEchoedTokenChars = 32;

    public int DefaultLimit { get; } = defaultLimit > 0 ? defaultLimit : throw new ArgumentOutOfRangeException(nameof(defaultLimit));

    public int MaxLimit { get; } = maxLimit >= defaultLimit ? maxLimit : throw new ArgumentOutOfRangeException(nameof(maxLimit));

    public CompiledFilter Compile(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var kinds = new List<int>();
        var authors = new List<string>();
        var hashtags = new List<string>();
        int? limit = null;

        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length > 0 && tokens[0].Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            throw new FilterParseException("KQL ('from ... where ...') is not available yet (leaf 1c). Use kind:, author:, #tag and limit: tokens.");
        }

        foreach (var token in tokens)
        {
            if (token.StartsWith('#'))
            {
                AddHashtag(hashtags, token[1..], token);
                continue;
            }

            var colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1)
            {
                throw new FilterParseException($"Unrecognized token '{Echo(token)}'. Expected kind:, author:, #tag or limit:.");
            }

            var key = token[..colon].ToLowerInvariant();
            var values = token[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length == 0)
            {
                throw new FilterParseException($"'{Echo(token)}' has no value.");
            }

            switch (key)
            {
                case "kind" or "kinds":
                    foreach (var value in values)
                    {
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kind) || kind > MaxKind)
                        {
                            throw new FilterParseException($"'{Echo(value)}' is not a kind (0-{MaxKind}).");
                        }

                        if (!kinds.Contains(kind))
                        {
                            kinds.Add(kind);
                        }
                    }

                    break;

                case "author" or "authors":
                    foreach (var value in values)
                    {
                        if (value.StartsWith("npub1", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new FilterParseException("npub is not accepted yet; use the 64-character hex pubkey.");
                        }

                        var hex = value.ToLowerInvariant();
                        if (!EventVerifier.IsLowerHex(hex, 64))
                        {
                            throw new FilterParseException($"'{Echo(value)}' is not a 64-character hex pubkey.");
                        }

                        if (!authors.Contains(hex))
                        {
                            authors.Add(hex);
                        }
                    }

                    break;

                case "t" or "tag" or "tags":
                    foreach (var value in values)
                    {
                        AddHashtag(hashtags, value.StartsWith('#') ? value[1..] : value, token);
                    }

                    break;

                case "limit":
                    if (values.Length != 1 || !int.TryParse(values[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedLimit) || parsedLimit < 1)
                    {
                        throw new FilterParseException($"'{Echo(token)}' is not a positive limit.");
                    }

                    limit = Math.Min(parsedLimit, MaxLimit);
                    break;

                default:
                    throw new FilterParseException($"Unknown key '{Echo(key)}'. Expected kind:, author:, #tag or limit:.");
            }
        }

        var filter = new NostrFilter(
            Authors: authors.Count > 0 ? authors : null,
            Kinds: kinds.Count > 0 ? kinds : [DefaultKind],
            TagFilters: hashtags.Count > 0 ? new Dictionary<string, IReadOnlyList<string>> { ["t"] = hashtags } : null,
            Limit: limit ?? DefaultLimit);
        return new CompiledFilter(filter);
    }

    private static void AddHashtag(List<string> hashtags, string raw, string token)
    {
        if (raw.Length == 0 || raw.Length > MaxHashtagChars)
        {
            throw new FilterParseException($"'{Echo(token)}' is not a hashtag (1-{MaxHashtagChars} characters after '#').");
        }

        var hashtag = raw.ToLowerInvariant();
        if (!hashtags.Contains(hashtag))
        {
            hashtags.Add(hashtag);
        }
    }

    /// <summary>User-typed text echoed back in an error, bounded so a pasted blob cannot balloon the message.</summary>
    private static string Echo(string text) => text.Length <= MaxEchoedTokenChars ? text : string.Concat(text.AsSpan(0, MaxEchoedTokenChars), "…");
}
