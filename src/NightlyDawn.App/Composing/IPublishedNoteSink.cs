using NightlyDawn.Core;

namespace NightlyDawn.App.Composing;

/// <summary>
/// Where a successful publish lands (plan §10.1-4): inserted into the active timeline column, with a
/// relay-acceptance status. A narrow interface -- not a direct <see cref="Timelines.TimelineColumnViewModel"/>
/// dependency -- so <see cref="ComposeViewModel"/> and <see cref="NoteRowActions"/> stay testable with a fake
/// instead of a real column.
///
/// <para>Called on success only: <see cref="EventPublishException"/> (0-of-N accepted) is thrown by the
/// publisher before a <see cref="PublishResult"/> could ever reach here, so there is no "insert, then roll
/// back" path to get wrong.</para>
/// </summary>
public interface IPublishedNoteSink
{
    void NotePublished(Note note, PublishResult result);
}
