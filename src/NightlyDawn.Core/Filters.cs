namespace NightlyDawn.Core;

// KQL "from SOURCES where EXPRESSIONS" (plan §4). The lexer/parser/compiler
// implementation is phase 1c (NightlyDawn.Filters); this is only the shape
// phase 1 leaves build against.

public enum FilterSourceKind
{
    Home,
    Mentions,
    User,
    List,
    Search,
    Relay,
    Kind,
}

/// <summary>A KQL source, e.g. <c>user(npub1...)</c> or <c>search("nostr")</c>. <see cref="Argument"/> holds the raw argument text (npub/naddr/search term/relay url/kind number) for the compiler to parse further.</summary>
public sealed record FilterSource(FilterSourceKind Kind, string? Argument = null);

public enum FilterComparisonOperator
{
    Equals,
    NotEquals,
    Contains,
    GreaterThan,
    LessThan,
}

public abstract record FilterNode;

/// <summary><paramref name="Field"/> is one of plan §4's field names: text, kind, created_at, user.npub, user.name, user.nip05, tags.t, reply, root, reactions, reposts, relay.</summary>
public sealed record FilterComparison(string Field, FilterComparisonOperator Operator, string Value) : FilterNode;

public sealed record FilterNot(FilterNode Operand) : FilterNode;

public sealed record FilterAnd(FilterNode Left, FilterNode Right) : FilterNode;

public sealed record FilterOr(FilterNode Left, FilterNode Right) : FilterNode;

public sealed record FilterAst(FilterSource Source, FilterNode? Where = null);

/// <summary>Result of compiling a <see cref="FilterAst"/>: the subset relays can evaluate (authors/kinds/#t/since) plus whatever must still be checked client-side. <see cref="LocalPredicate"/> is null when the relay filter alone is sufficient.</summary>
public sealed record CompiledFilter(NostrFilter RelayFilter, Func<Note, bool>? LocalPredicate = null);
