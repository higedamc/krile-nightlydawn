using NightlyDawn.Core;

namespace NightlyDawn.Filters;

/// <summary>
/// Turns a <see cref="FilterAst"/> into a <see cref="CompiledFilter"/>. Plan §7.2's invariant: <c>RelayFilter</c>
/// must be a superset of what the AST accepts (dropping even one note the AST would keep is unsound), so
/// <c>LocalPredicate</c> always re-evaluates the *entire* <see cref="FilterAst.Where"/> when it is non-null —
/// pushing a condition down into <c>RelayFilter</c> is purely a bandwidth optimization layered on top of that
/// guarantee, never a substitute for it. Whenever two push-down candidates collide on the same
/// <see cref="NostrFilter"/> field, this file resolves the collision in whichever direction keeps the filter a
/// superset (union for list fields, the tighter bound only within the same top-level AND chain for <c>Since</c>);
/// see <see cref="PushDown"/>.
/// </summary>
internal static class KqlCompiler
{
    private const int MaxKind = 65_535;

    private static readonly HashSet<string> AlwaysUnsupportedFields = new(StringComparer.Ordinal)
    {
        // Need a Profile lookup Compile isn't wired to (plan §1 item B).
        "user.name", "user.nip05",
        // Need counts this client never collects (plan §1 item B).
        "reactions", "reposts",
        // Note.FirstSeenOnRelay is the relay that delivered the event first, with no accumulated list of every
        // relay that ever carried it — deliberately, so there is nothing a `where relay = …` comparison could
        // check beyond that one value, which is exactly what `from relay(…)` already expresses as a source
        // (see Entities.cs's Note doc comment, decided 2026-10-07, predating this leaf).
        "relay",
    };

    public static CompiledFilter Compile(FilterAst ast)
    {
        ArgumentNullException.ThrowIfNull(ast);

        if (ast.Where is not null)
        {
            RejectUnsupportedFields(ast.Where);
        }

        var kinds = new List<int>();
        var authors = new List<string>();
        var tagT = new List<string>();
        long? since = null;
        Func<Note, bool>? sourcePredicate = null;

        switch (ast.Source.Kind)
        {
            case FilterSourceKind.Home:
                throw new UnsupportedFilterFieldException("from home");
            case FilterSourceKind.Mentions:
                throw new UnsupportedFilterFieldException("from mentions");
            case FilterSourceKind.List:
                throw new UnsupportedFilterFieldException("from list");
            case FilterSourceKind.User:
                authors.Add(ResolvePubkeyHex(ast.Source.Argument!));
                break;
            case FilterSourceKind.Kind:
                kinds.Add(ParseKind(ast.Source.Argument!));
                break;
            case FilterSourceKind.Search:
            {
                // NostrFilter.Search (NIP-50) is never emitted: not every relay implements it, and relays that
                // do may stem/fuzzy-match, which is narrower than our own Contains — unsound either way
                // (plan §7.2). `from search(...)` is instead exactly a local `text contains "..."` predicate.
                var term = ast.Source.Argument!;
                sourcePredicate = note => note.Content.Contains(term, StringComparison.Ordinal);
                break;
            }

            case FilterSourceKind.Relay:
            {
                // No NostrFilter field corresponds to "which relay delivered this" — it is a subscription-level
                // choice, not a per-event server-side filter — so this is a local predicate against the one
                // relay Note.FirstSeenOnRelay actually records (plan §7.2).
                var relayUrl = ParseRelayArgument(ast.Source.Argument!);
                sourcePredicate = note => note.FirstSeenOnRelay == relayUrl;
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(ast), ast.Source.Kind, "Unknown FilterSourceKind.");
        }

        if (ast.Where is not null)
        {
            PushDown(ast.Where, kinds, authors, tagT, ref since);
        }

        var relayFilter = new NostrFilter(
            Authors: authors.Count > 0 ? authors : null,
            Kinds: kinds.Count > 0 ? kinds : null,
            TagFilters: tagT.Count > 0 ? new Dictionary<string, IReadOnlyList<string>> { ["t"] = tagT } : null,
            Since: since);

        Func<Note, bool>? wherePredicate = ast.Where is null ? null : note => Evaluate(ast.Where, note);
        var localPredicate = Combine(sourcePredicate, wherePredicate);

        return new CompiledFilter(relayFilter, localPredicate);
    }

    private static Func<Note, bool>? Combine(Func<Note, bool>? a, Func<Note, bool>? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return note => a(note) && b(note);
    }

    // Walks every branch, including under Not/Or — a field this version cannot evaluate is just as unevaluable
    // there as at the top level, because LocalPredicate (once built) will evaluate the *entire* Where tree
    // regardless of position (plan §7.3: "the branch isn't pushed down, so it's safe to ignore" is wrong).
    private static void RejectUnsupportedFields(FilterNode node)
    {
        switch (node)
        {
            case FilterAnd(var left, var right):
                RejectUnsupportedFields(left);
                RejectUnsupportedFields(right);
                break;
            case FilterOr(var left, var right):
                RejectUnsupportedFields(left);
                RejectUnsupportedFields(right);
                break;
            case FilterNot(var operand):
                RejectUnsupportedFields(operand);
                break;
            case FilterComparison(var field, _, _):
                if (AlwaysUnsupportedFields.Contains(field))
                {
                    throw new UnsupportedFilterFieldException(field);
                }

                break;
        }
    }

    // Only the top-level AND chain is walked — Or/Not branches are never pushed down, because narrowing
    // RelayFilter inside either would make it stop being a superset (plan §7.2). Collisions on the same field
    // (e.g. two `kind = n` comparisons at the top level) union rather than intersect: intersecting could narrow
    // below what the AST actually accepts if read wrong, while union can only ever make RelayFilter broader,
    // which stays sound because LocalPredicate re-checks the exact condition regardless.
    private static void PushDown(FilterNode node, List<int> kinds, List<string> authors, List<string> tagT, ref long? since)
    {
        switch (node)
        {
            case FilterAnd(var left, var right):
                PushDown(left, kinds, authors, tagT, ref since);
                PushDown(right, kinds, authors, tagT, ref since);
                break;
            case FilterComparison("kind", FilterComparisonOperator.Equals, var value):
            {
                var kind = ParseKind(value);
                if (!kinds.Contains(kind))
                {
                    kinds.Add(kind);
                }

                break;
            }

            case FilterComparison("user.npub", FilterComparisonOperator.Equals, var value):
            {
                var hex = ResolvePubkeyHex(value);
                if (!authors.Contains(hex))
                {
                    authors.Add(hex);
                }

                break;
            }

            case FilterComparison("tags.t", FilterComparisonOperator.Equals, var value):
            {
                var tag = value.ToLowerInvariant();
                if (!tagT.Contains(tag))
                {
                    tagT.Add(tag);
                }

                break;
            }

            case FilterComparison("created_at", FilterComparisonOperator.GreaterThan, var value):
            {
                // NIP-01's `since` is inclusive (>=); our AST's GreaterThan is strict (>). Pushing `v` into
                // Since=v therefore asks relays for a one-event-wider range than strictly required — a safe
                // superset, corrected by LocalPredicate re-checking the strict `>` (plan §7.2).
                var v = ParseLong(value, "created_at");
                since = since is null ? v : Math.Max(since.Value, v);
                break;
            }

            // Anything else (Or, Not, other fields/operators on these same names) is not pushed down. It is
            // still correctly handled, because LocalPredicate evaluates the whole Where tree unconditionally.
        }
    }

    private static bool Evaluate(FilterNode node, Note note) => node switch
    {
        FilterAnd(var left, var right) => Evaluate(left, note) && Evaluate(right, note),
        FilterOr(var left, var right) => Evaluate(left, note) || Evaluate(right, note),
        FilterNot(var operand) => !Evaluate(operand, note),
        FilterComparison comparison => EvaluateComparison(comparison, note),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown FilterNode."),
    };

    private static bool EvaluateComparison(FilterComparison comparison, Note note)
    {
        var (field, op, value) = comparison;
        return field switch
        {
            "text" => CompareString(note.Content, op, value),
            "kind" => CompareLong(field, (long)note.Kind, op, ParseLong(value, "kind")),
            "created_at" => CompareLong(field, note.CreatedAt, op, ParseLong(value, "created_at")),
            "user.npub" => CompareString(note.AuthorPubkey, op, ResolvePubkeyHex(value)),
            "tags.t" => CompareTagSet(note.Hashtags, op, value.ToLowerInvariant()),
            "reply" => CompareBool(field, note.ReplyId is not null, op, value),
            "root" => CompareBool(field, note.RootId is not null, op, value),
            _ => throw new InvalidOperationException(
                $"Unreachable: field '{field}' should have been rejected by Parse or RejectUnsupportedFields before evaluation."),
        };
    }

    private static bool CompareString(string actual, FilterComparisonOperator op, string value) => op switch
    {
        FilterComparisonOperator.Equals => string.Equals(actual, value, StringComparison.Ordinal),
        FilterComparisonOperator.NotEquals => !string.Equals(actual, value, StringComparison.Ordinal),
        FilterComparisonOperator.Contains => actual.Contains(value, StringComparison.Ordinal),
        FilterComparisonOperator.GreaterThan => string.CompareOrdinal(actual, value) > 0,
        FilterComparisonOperator.LessThan => string.CompareOrdinal(actual, value) < 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown FilterComparisonOperator."),
    };

    private static bool CompareLong(string field, long actual, FilterComparisonOperator op, long value) => op switch
    {
        FilterComparisonOperator.Equals => actual == value,
        FilterComparisonOperator.NotEquals => actual != value,
        FilterComparisonOperator.GreaterThan => actual > value,
        FilterComparisonOperator.LessThan => actual < value,
        FilterComparisonOperator.Contains => throw new FilterParseException($"'{field}' is numeric; 'contains' is not a valid comparison for it."),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown FilterComparisonOperator."),
    };

    private static bool CompareBool(string field, bool actual, FilterComparisonOperator op, string value)
    {
        var expected = value.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FilterParseException($"'{field}' is a boolean field; its value must be 'true' or 'false', got '{value}'."),
        };

        return op switch
        {
            FilterComparisonOperator.Equals => actual == expected,
            FilterComparisonOperator.NotEquals => actual != expected,
            _ => throw new FilterParseException($"'{field}' is a boolean field; only '=' and '!=' are valid comparisons for it."),
        };
    }

    private static bool CompareTagSet(IReadOnlyList<string>? hashtags, FilterComparisonOperator op, string value) => op switch
    {
        FilterComparisonOperator.Equals => hashtags?.Contains(value, StringComparer.Ordinal) ?? false,
        FilterComparisonOperator.NotEquals => !(hashtags?.Contains(value, StringComparer.Ordinal) ?? false),
        FilterComparisonOperator.Contains => hashtags?.Any(t => t.Contains(value, StringComparison.Ordinal)) ?? false,
        _ => throw new FilterParseException("'tags.t' is a set; only '=', '!=' and 'contains' are valid comparisons for it."),
    };

    /// <exception cref="FilterParseException">Not a valid 64-character hex pubkey or <c>npub1…</c> (plan §7.4: validate before trusting the argument).</exception>
    private static string ResolvePubkeyHex(string raw)
    {
        if (raw.StartsWith("npub1", StringComparison.OrdinalIgnoreCase))
        {
            if (!Bech32.TryDecode("npub", raw.ToLowerInvariant(), out var payload) || payload.Length != 32)
            {
                throw new FilterParseException($"'{Truncate(raw)}' is not a valid npub.");
            }

            return Convert.ToHexString(payload).ToLowerInvariant();
        }

        var lower = raw.ToLowerInvariant();
        if (!IsLowerHex64(lower))
        {
            throw new FilterParseException($"'{Truncate(raw)}' is not a 64-character hex pubkey or an npub.");
        }

        return lower;
    }

    private static bool IsLowerHex64(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <exception cref="FilterParseException">Not an integer in [0, 65535] (NIP-01's kind range).</exception>
    private static int ParseKind(string raw)
    {
        if (!int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kind) || kind > MaxKind)
        {
            throw new FilterParseException($"'{Truncate(raw)}' is not a kind (0-{MaxKind}).");
        }

        return kind;
    }

    /// <exception cref="FilterParseException">Not a 64-bit integer.</exception>
    private static long ParseLong(string raw, string field)
    {
        if (!long.TryParse(raw, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new FilterParseException($"'{field}' must be an integer, got '{Truncate(raw)}'.");
        }

        return value;
    }

    /// <exception cref="FilterParseException">Not an absolute <c>wss://</c> URL (or <c>ws://</c>; plan §5 S3 development opt-in is not exposed here since a persisted query is never a developer-only context).</exception>
    private static RelayUrl ParseRelayArgument(string raw)
    {
        try
        {
            return RelayUrl.Parse(raw);
        }
        catch (ArgumentException ex)
        {
            throw new FilterParseException($"'{Truncate(raw)}' is not a valid relay URL: {ex.Message}");
        }
    }

    /// <summary>User-typed text echoed back in an error, bounded so a pasted blob cannot balloon the message (mirrors SimpleTimelineQueryCompiler.Echo).</summary>
    private static string Truncate(string text) => text.Length <= 32 ? text : string.Concat(text.AsSpan(0, 32), "…");
}
