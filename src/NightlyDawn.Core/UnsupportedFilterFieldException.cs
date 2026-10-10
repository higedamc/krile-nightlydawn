namespace NightlyDawn.Core;

/// <summary>
/// L5 v1 cannot compile these KQL fields: <c>user.name</c> and <c>user.nip05</c> need a <see cref="Profile"/>
/// lookup the compiler isn't wired to yet, and <c>reactions</c> and <c>reposts</c> need counts this client
/// never collects. Thrown from <see cref="IFilterCompiler.Compile"/>, never from
/// <see cref="IFilterCompiler.Parse"/> -- the input parsed correctly (the grammar accepts these fields), so
/// this is deliberately not a <see cref="FilterParseException"/>: calling it a parse error would be false to
/// both the caller and anyone reading the message (plan §1 item B, Lead's call).
/// </summary>
public sealed class UnsupportedFilterFieldException(string field)
    : NightlyDawnException($"The KQL field '{field}' is not supported in this version.")
{
    public string Field { get; } = field;
}
