using NightlyDawn.App.Composing;
using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>Records every <see cref="IPublishedNoteSink.NotePublished"/> call -- the real sink is
/// <see cref="NightlyDawn.App.Timelines.TimelineColumnViewModel"/>, but <see cref="ComposeViewModel"/> and
/// <see cref="NoteRowActions"/> only need the narrow interface (plan §10.1-4).</summary>
internal sealed class FakePublishedNoteSink : IPublishedNoteSink
{
    public List<(Note Note, PublishResult Result)> Published { get; } = [];

    public void NotePublished(Note note, PublishResult result) => Published.Add((note, result));
}
