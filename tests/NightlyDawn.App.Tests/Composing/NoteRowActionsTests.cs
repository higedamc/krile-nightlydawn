using NightlyDawn.App.Composing;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests.Composing;

public class NoteRowActionsTests
{
    private static readonly Note TextTarget = TestNotes.Make(1, 1);

    private static (NoteRowActions Actions, FakeNotePublisher Publisher, FakePublishedNoteSink Sink, ComposeViewModel Compose, List<string> Statuses) New(Note? note = null)
    {
        var publisher = new FakeNotePublisher();
        var sink = new FakePublishedNoteSink();
        var compose = new ComposeViewModel(() => publisher, () => sink, postToUi: action => action());
        var statuses = new List<string>();
        var actions = new NoteRowActions(note ?? TextTarget, () => publisher, compose, sink, statuses.Add);
        return (actions, publisher, sink, compose, statuses);
    }

    [Fact]
    public async Task RepostAsync_OnSuccess_InsertsIntoTheSink_AndReportsAcceptedCount()
    {
        var (actions, publisher, sink, _, statuses) = New();
        publisher.NextOutcomes = [new(RelayUrl.Parse("wss://a/"), true)];

        await actions.RepostAsync();

        Assert.Equal("Repost", Assert.Single(publisher.Calls).Method);
        Assert.Single(sink.Published);
        Assert.Equal("Reposted · 1/1 relays accepted.", Assert.Single(statuses));
    }

    [Fact]
    public async Task RepostAsync_OnEventPublishException_DoesNotThrow_AndDoesNotInsertIntoTheSink()
    {
        // Negative control (plan §10.3 ①/③): remove the catch in NoteRowActions.RepostAsync and this throws
        // out of the test instead of recording a status.
        var (actions, publisher, sink, _, statuses) = New();
        publisher.RejectWith = new EventPublishException(new PublishResult([]));

        await actions.RepostAsync();

        Assert.Equal("Repost failed: 0 of 0 relays accepted.", Assert.Single(statuses));
        Assert.Empty(sink.Published);
    }

    [Fact]
    public async Task RepostAsync_CalledTwiceWithoutAwaitingTheFirst_PublishesOnlyOnce()
    {
        // Negative control (plan §10.3 ②): removing the _repostBusy guard makes publisher.CallCount come back
        // 2 instead of 1. The Gate holds the first call suspended mid-publish -- without it, FakeNotePublisher
        // would resolve synchronously and the guard would look like it works even if it were deleted, since
        // there would be no real await for a second call to race against (a real relay round trip always has
        // this gap; this fake only does with Gate set).
        var (actions, publisher, _, _, _) = New();
        publisher.Gate = new TaskCompletionSource();

        var first = actions.RepostAsync();
        var second = actions.RepostAsync();
        Assert.Equal(1, publisher.CallCount); // The second call never even reached the publisher.

        publisher.Gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, publisher.CallCount);
    }

    [Fact]
    public async Task ReactAsync_SendsAFixedPlusContent_RegardlessOfCaller()
    {
        var (actions, publisher, _, _, statuses) = New();

        await actions.ReactAsync();

        var call = Assert.Single(publisher.Calls);
        Assert.Equal("React", call.Method);
        Assert.Equal("+", call.Content);
        Assert.Equal("Reacted · 1/1 relays accepted.", Assert.Single(statuses));
    }

    [Fact]
    public async Task ReactAsync_CalledTwiceWithoutAwaitingTheFirst_PublishesOnlyOnce()
    {
        // Same reasoning as the Repost negative control above: Gate forces a real suspension for the guard to race against.
        var (actions, publisher, _, _, _) = New();
        publisher.Gate = new TaskCompletionSource();

        var first = actions.ReactAsync();
        var second = actions.ReactAsync();
        Assert.Equal(1, publisher.CallCount);

        publisher.Gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, publisher.CallCount);
    }

    [Fact]
    public void BeginReply_PutsTheComposeBoxIntoReplyMode_TargetingThisRow()
    {
        var (actions, _, _, compose, _) = New();

        actions.BeginReply();

        Assert.Equal(ComposeMode.Reply, compose.Mode);
        Assert.Same(TextTarget, compose.Target);
    }

    [Fact]
    public void BeginQuote_PutsTheComposeBoxIntoQuoteMode_TargetingThisRow()
    {
        var (actions, _, _, compose, _) = New();

        actions.BeginQuote();

        Assert.Equal(ComposeMode.Quote, compose.Mode);
        Assert.Same(TextTarget, compose.Target);
    }
}
