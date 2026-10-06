using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// Turns a column's query text (<see cref="Timeline.KqlQuery"/>) into a <see cref="CompiledFilter"/>. Leaf 1e-b ships
/// <see cref="SimpleTimelineQueryCompiler"/>; when 1c lands, an adapter over <see cref="IFilterCompiler"/>
/// (<c>Parse</c> then <c>Compile</c>) replaces it here and nothing in the timeline source changes.
/// </summary>
public interface ITimelineQueryCompiler
{
    /// <exception cref="FilterParseException">The query text is not understood.</exception>
    CompiledFilter Compile(string query);
}
