using NightlyDawn.Core;

namespace NightlyDawn.App.Tests.Fakes;

/// <summary>
/// Scriptable <see cref="INotePublisher"/>: no signer, no relay, no network. <see cref="RejectWith"/> makes
/// every call fail with that exception (mirrors <see cref="NightlyDawn.Nostr.Publishing.NotePublisher"/>'s
/// real failure modes -- <see cref="ArgumentException"/> for input validation, <see cref="EventPublishException"/>
/// for 0-of-N accepted) without this fake needing to reproduce that class's own validation rules.
/// </summary>
internal sealed class FakeNotePublisher : INotePublisher
{
    public List<(string Method, Note? Target, string Content)> Calls { get; } = [];

    public Exception? RejectWith { get; set; }

    /// <summary>Relay outcomes the next successful call returns; defaults to one relay accepting.</summary>
    public IReadOnlyList<RelayPublishOutcome> NextOutcomes { get; set; } = [new RelayPublishOutcome(RelayUrl.Parse("wss://fake.example/"), Accepted: true)];

    /// <summary>When set, every call suspends here until the test completes this source -- otherwise a call
    /// that completes synchronously (the default) can make a caller's "second call sees the first still
    /// in flight" guard look correct even with the guard removed, since there is no real await for the
    /// second call to race against. A real <see cref="NightlyDawn.Nostr.Publishing.NotePublisher"/> always
    /// has this gap (signing, then a real relay round trip); this fake only has one when a test asks for it.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public int CallCount => Calls.Count;

    public Task<PublishedNote> PostNoteAsync(string content, CancellationToken cancellationToken = default) =>
        Invoke("Post", null, content);

    public Task<PublishedNote> ReplyToAsync(Note parent, string content, CancellationToken cancellationToken = default) =>
        Invoke("Reply", parent, content);

    public Task<PublishedNote> RepostAsync(Note target, CancellationToken cancellationToken = default) =>
        Invoke("Repost", target, string.Empty);

    public Task<PublishedNote> QuoteAsync(Note quoted, string content, CancellationToken cancellationToken = default) =>
        Invoke("Quote", quoted, content);

    public Task<PublishedNote> ReactAsync(Note target, string content = "+", CancellationToken cancellationToken = default) =>
        Invoke("React", target, content);

    private async Task<PublishedNote> Invoke(string method, Note? target, string content)
    {
        Calls.Add((method, target, content));
        if (Gate is { } gate)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (RejectWith is { } exception)
        {
            throw exception;
        }

        return new PublishedNote(target ?? TestNotes.Make(99, 1, content: content), new PublishResult(NextOutcomes));
    }
}
