namespace NightlyDawn.Core;

/// <summary>
/// The query's source (<c>from home</c> or <c>from mentions</c>) needs a signed-in account's pubkey to compile,
/// and none was given. Thrown by <see cref="IFilterCompiler.Compile"/> and by anything compiling a timeline query
/// on its behalf (e.g. <c>ITimelineSourceFactory.CreateAnonymous</c>). Deliberately its own type, not
/// <see cref="FilterParseException"/> or <see cref="UnsupportedFilterFieldException"/>: the query is syntactically
/// valid and the field is supported, so neither of those would be true, and conflating "no account" with "zero
/// results" or "0c needs a real signer" via some other mechanism would hide from the user that signing in is the
/// fix (plan §13.2 item 5).
/// </summary>
public sealed class AccountRequiredException(FilterSourceKind source)
    : NightlyDawnException($"'from {source.ToString().ToLowerInvariant()}' needs a signed-in account.")
{
    public FilterSourceKind SourceKind { get; } = source;
}
