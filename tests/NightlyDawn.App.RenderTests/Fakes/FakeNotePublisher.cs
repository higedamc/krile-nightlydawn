using NightlyDawn.Core;

namespace NightlyDawn.App.RenderTests.Fakes;

/// <summary>
/// Minimal scriptable <see cref="INotePublisher"/> for the routing test: no signer, no relay, no network.
/// One relay accepts every call -- these tests only need a successful round trip to reach
/// <see cref="Timelines.TimelineViewModel.NotePublished"/>/<see cref="Timelines.TimelineViewModel.ReportStatus"/>,
/// not the failure paths <c>NightlyDawn.App.Tests.Fakes.FakeNotePublisher</c> covers (that one is internal to
/// its own assembly and not visible here).
/// </summary>
internal sealed class FakeNotePublisher : INotePublisher
{
    private static readonly IReadOnlyList<RelayPublishOutcome> OneRelayAccepted =
        [new RelayPublishOutcome(RelayUrl.Parse("wss://fake.example/"), Accepted: true)];

    public Task<PublishedNote> PostNoteAsync(string content, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublishedNote(MakeNote(content), new PublishResult(OneRelayAccepted)));

    public Task<PublishedNote> ReplyToAsync(Note parent, string content, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublishedNote(MakeNote(content), new PublishResult(OneRelayAccepted)));

    public Task<PublishedNote> RepostAsync(Note target, CancellationToken cancellationToken = default) =>
        // A repost is a distinct event (new id), not the original note re-returned -- RepostAsync_CalledTwice...
        // in NoteRowActionsTests guards the same distinction, and the routing test below needs a fresh id too:
        // TimelineViewModel.AddNote is a no-op for an id it already knows about (the target is already
        // in the column), so returning `target` unchanged would make the routing assertion pass even if the
        // repost silently landed in the wrong column.
        Task.FromResult(new PublishedNote(MakeNote($"(repost) {target.Content}"), new PublishResult(OneRelayAccepted)));

    public Task<PublishedNote> QuoteAsync(Note quoted, string content, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublishedNote(MakeNote(content), new PublishResult(OneRelayAccepted)));

    public Task<PublishResult> ReactAsync(Note target, string content = "+", CancellationToken cancellationToken = default) =>
        Task.FromResult(new PublishResult(OneRelayAccepted));

    private static Note MakeNote(string content) => new(
        Id: Guid.NewGuid().ToString("N"),
        AuthorPubkey: new string('a', 64),
        CreatedAt: 1_700_000_000,
        Kind: NoteKind.Text,
        Content: content,
        Tags: []);
}
