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
/// <para>A source (<c>user(...)</c>/<c>kind(...)</c>/<c>search(...)</c>/<c>relay(...)</c>) is a hard restriction,
/// not just a relay-filter hint — a union push-down (above) can only widen <c>RelayFilter</c> to ask for more than
/// the source alone would, so every source also contributes its own clause to <c>LocalPredicate</c>
/// (<see cref="Compile"/>'s <c>sourcePredicate</c>) re-asserting that restriction locally. Without it, e.g.
/// <c>from user(A) where user.npub = B</c> would union <c>Authors</c> to <c>[A, B]</c> and <c>LocalPredicate</c>
/// (built only from <c>Where</c>) would accept B's notes in a column that asked for A's (plan §7.6 item ②).</para>
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
            {
                var hex = ResolvePubkeyHex(ast.Source.Argument!);
                authors.Add(hex);
                sourcePredicate = note => note.AuthorPubkey == hex;
                break;
            }

            case FilterSourceKind.Kind:
            {
                var kind = ParseKind(ast.Source.Argument!);
                kinds.Add(kind);
                sourcePredicate = note => (int)note.Kind == kind;
                break;
            }

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

        if (kinds.Count == 0)
        {
            // A Note can only ever represent kind 1/6/16 (NoteKind) — no event of any other kind can become
            // one — so defaulting here can never drop a Note the AST would otherwise have accepted. It only
            // trims relay bandwidth for queries that place no constraint on kind themselves, e.g. a bare
            // `from search(...)` or `from relay(...)` with no `where kind = ...` (plan §7.6 item ④).
            kinds.AddRange([(int)NoteKind.Text, (int)NoteKind.Repost, (int)NoteKind.GenericRepost]);
        }

        var relayFilter = new NostrFilter(
            Authors: authors.Count > 0 ? authors : null,
            Kinds: kinds.Count > 0 ? kinds : null,
            TagFilters: tagT.Count > 0 ? new Dictionary<string, IReadOnlyList<string>> { ["t"] = tagT } : null,
            Since: since);

        Func<Note, bool>? wherePredicate = ast.Where is null ? null : CompileWhere(ast.Where);
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
                var tag = NormalizeHashtag(value);
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

    /// <summary>Normalizes a <c>tags.t</c> value for comparison. Both <see cref="PushDown"/> (the value sent to
    /// relays) and <see cref="CompileComparison"/> (the value compared against <c>Note.Hashtags</c> locally) call
    /// this one helper, so the two sides can never drift apart (plan §7.6 item ③). This depends on
    /// <c>Note.Hashtags</c> always being lowercased the same way — see that record's doc comment in
    /// Entities.cs — to keep RelayFilter and LocalPredicate comparing byte-identical values on both sides.</summary>
    private static string NormalizeHashtag(string value) => value.ToLowerInvariant();

    // Compiles the entire Where tree once, up front, rather than re-parsing each comparison's value every time a
    // note is evaluated. A malformed value (e.g. `created_at < abc`) or an operator invalid for its field (e.g.
    // `kind contains "1"`) therefore fails here — at Compile — instead of partway through a live timeline on
    // whichever note happens to be evaluated first (plan §7.6 item ①). The resulting closures capture the
    // already-parsed value, so evaluation itself never re-parses or re-validates.
    private static Func<Note, bool> CompileWhere(FilterNode node) => node switch
    {
        FilterAnd(var left, var right) => And(CompileWhere(left), CompileWhere(right)),
        FilterOr(var left, var right) => Or(CompileWhere(left), CompileWhere(right)),
        FilterNot(var operand) => Negate(CompileWhere(operand)),
        FilterComparison comparison => CompileComparison(comparison),
        _ => throw new ArgumentOutOfRangeException(nameof(node), node, "Unknown FilterNode."),
    };

    private static Func<Note, bool> And(Func<Note, bool> left, Func<Note, bool> right) => note => left(note) && right(note);

    private static Func<Note, bool> Or(Func<Note, bool> left, Func<Note, bool> right) => note => left(note) || right(note);

    private static Func<Note, bool> Negate(Func<Note, bool> operand) => note => !operand(note);

    /// <exception cref="FilterParseException">The comparison's operator or value is invalid for its field's type.</exception>
    private static Func<Note, bool> CompileComparison(FilterComparison comparison)
    {
        var (field, op, value) = comparison;
        switch (field)
        {
            case "text":
                return note => CompareString(note.Content, op, value);

            case "kind":
            {
                RejectContains(field, op);
                var expected = ParseLong(value, field);
                return note => CompareLong((long)note.Kind, op, expected);
            }

            case "created_at":
            {
                RejectContains(field, op);
                var expected = ParseLong(value, field);
                return note => CompareLong(note.CreatedAt, op, expected);
            }

            case "user.npub":
            {
                var hex = ResolvePubkeyHex(value);
                return note => CompareString(note.AuthorPubkey, op, hex);
            }

            case "tags.t":
            {
                if (op is not (FilterComparisonOperator.Equals or FilterComparisonOperator.NotEquals or FilterComparisonOperator.Contains))
                {
                    throw new FilterParseException("'tags.t' is a set; only '=', '!=' and 'contains' are valid comparisons for it.");
                }

                var tag = NormalizeHashtag(value);
                return note => CompareTagSet(note.Hashtags, op, tag);
            }

            case "reply":
                return CompileBooleanField(field, op, value, note => note.ReplyId is not null);

            case "root":
                return CompileBooleanField(field, op, value, note => note.RootId is not null);

            default:
                throw new InvalidOperationException(
                    $"Unreachable: field '{field}' should have been rejected by Parse or RejectUnsupportedFields before this point.");
        }
    }

    private static void RejectContains(string field, FilterComparisonOperator op)
    {
        if (op == FilterComparisonOperator.Contains)
        {
            throw new FilterParseException($"'{field}' is numeric; 'contains' is not a valid comparison for it.");
        }
    }

    private static Func<Note, bool> CompileBooleanField(string field, FilterComparisonOperator op, string value, Func<Note, bool> actual)
    {
        if (op is not (FilterComparisonOperator.Equals or FilterComparisonOperator.NotEquals))
        {
            throw new FilterParseException($"'{field}' is a boolean field; only '=' and '!=' are valid comparisons for it.");
        }

        var expected = value.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FilterParseException($"'{field}' is a boolean field; its value must be 'true' or 'false', got '{value}'."),
        };

        return op == FilterComparisonOperator.Equals
            ? note => actual(note) == expected
            : note => actual(note) != expected;
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

    private static bool CompareLong(long actual, FilterComparisonOperator op, long value) => op switch
    {
        FilterComparisonOperator.Equals => actual == value,
        FilterComparisonOperator.NotEquals => actual != value,
        FilterComparisonOperator.GreaterThan => actual > value,
        FilterComparisonOperator.LessThan => actual < value,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown FilterComparisonOperator."),
    };

    private static bool CompareTagSet(IReadOnlyList<string>? hashtags, FilterComparisonOperator op, string value) => op switch
    {
        FilterComparisonOperator.Equals => hashtags?.Contains(value, StringComparer.Ordinal) ?? false,
        FilterComparisonOperator.NotEquals => !(hashtags?.Contains(value, StringComparer.Ordinal) ?? false),
        FilterComparisonOperator.Contains => hashtags?.Any(t => t.Contains(value, StringComparison.Ordinal)) ?? false,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown FilterComparisonOperator."),
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
