using NightlyDawn.App.Composing;
using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests.Composing;

/// <summary>Exercises the compose box without Avalonia: <c>postToUi</c> runs actions inline (same pattern as
/// <c>TimelineColumnViewModelTests</c>), so state is observable directly after each await.</summary>
public class ComposeViewModelTests
{
    private static ComposeViewModel NewViewModel(FakeNotePublisher publisher, FakePublishedNoteSink sink) =>
        new(() => publisher, () => sink, postToUi: action => action());

    [Fact]
    public async Task PostAsync_OnSuccess_ClearsContent_InsertsIntoTheSink_AndReportsAcceptedCount()
    {
        var publisher = new FakeNotePublisher { NextOutcomes = [new(RelayUrl.Parse("wss://a/"), true), new(RelayUrl.Parse("wss://b/"), false)] };
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.Content = "hello nostr";

        await vm.PostAsync();

        Assert.Equal("Post", Assert.Single(publisher.Calls).Method);
        Assert.Equal(string.Empty, vm.Content);
        Assert.Equal("Posted · 1/2 relays accepted.", vm.Status);
        Assert.Single(sink.Published);
    }

    [Fact]
    public async Task PostAsync_OnArgumentException_DoesNotThrow_KeepsContent_AndShowsTheReasonVerbatim()
    {
        // Negative control (plan §10.3 ①/⑤): remove the catch in ComposeViewModel.RunAsync and this throws
        // instead of completing -- xUnit records the exception as a test failure.
        //
        // Built with the same two-argument constructor NotePublisher's own ValidateContent uses (message,
        // paramName) rather than a bare string: .NET's ArgumentException.Message appends " (Parameter
        // 'content')" when a paramName is given, and that suffix must survive to Status unmodified -- it is
        // part of "verbatim," not something this test should special-case away.
        var rejection = new ArgumentException("Content must not be empty or whitespace-only.", "content");
        var publisher = new FakeNotePublisher { RejectWith = rejection };
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.Content = "   ";

        await vm.PostAsync();

        Assert.Equal(rejection.Message, vm.Status);
        Assert.Equal("   ", vm.Content); // Not lost: a rejected post must still be there to edit and retry.
        Assert.Empty(sink.Published);
    }

    [Fact]
    public async Task PostAsync_OnEventPublishException_DoesNotThrow_AndDoesNotInsertIntoTheSink()
    {
        // Negative control (plan §10.3 ③): swallow this exception without reporting and sink.Published stays
        // empty either way, which would hide the bug -- the real assertion that matters is Status naming the
        // 0-of-N outcome, which a mutated "insert on any non-throwing path" implementation would also get wrong.
        var publisher = new FakeNotePublisher { RejectWith = new EventPublishException(new PublishResult([])) };
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.Content = "hello";

        await vm.PostAsync();

        Assert.Equal("Publish failed: 0 of 0 relays accepted.", vm.Status);
        Assert.Equal("hello", vm.Content);
        Assert.Empty(sink.Published);
    }

    [Fact]
    public async Task PostAsync_SecondCallWhileTheFirstIsInFlight_DoesNotPublishTwice()
    {
        // Negative control (plan §10.3 ②): removing the IsBusy guard in ComposeViewModel.PostAsync makes
        // publisher.Calls.Count come back 2 instead of 1.
        var publisher = new FakeNotePublisher();
        var gate = new TaskCompletionSource();
        var blockingPostToUi = new ManualResetPostToUi();
        var vm = new ComposeViewModel(() => publisher, () => new FakePublishedNoteSink(), blockingPostToUi.Post);
        vm.Content = "hello";

        var first = vm.PostAsync();
        var second = vm.PostAsync(); // IsBusy is already true synchronously by the time this runs (set before RunAsync's first await).

        blockingPostToUi.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(1, publisher.CallCount);
    }

    [Fact]
    public async Task PostAsync_OnAnUndocumentedPublisherException_DoesNotThrow_AndReportsTheTypeName()
    {
        // Negative control (plan §10.4-2): delete the trailing catch (Exception) in RunAsync and this throws
        // out of the test instead of completing with a status -- the same way SignerUnavailableException
        // (thrown before a key is ever unlocked, i.e. on first launch) currently escapes into Avalonia's
        // async-void click handler and takes the process down. Content must still survive, same as the
        // ArgumentException/EventPublishException paths above -- an undocumented exception is not a reason to
        // lose what the user typed either.
        var publisher = new FakeNotePublisher { RejectWith = new SignerUnavailableException("No local key is active.") };
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.Content = "hello";

        await vm.PostAsync();

        Assert.Equal("Post failed: SignerUnavailableException.", vm.Status);
        Assert.Equal("hello", vm.Content);
        Assert.Empty(sink.Published);
        Assert.True(vm.CanPost); // the finally still ran
    }

    [Fact]
    public async Task PostAsync_InReplyMode_CallsReplyToAsync_WithTheTargetAndContent()
    {
        // Negative control (plan §10.4-3): switch the Reply arm of RunAsync's mode switch to PostNoteAsync and
        // this fails on Method -- the prior suite (including the success/Busy/exception tests above) passes
        // either way, because none of them ever enter Reply/Quote mode first.
        var target = TestNotes.Make(5, 1);
        var publisher = new FakeNotePublisher();
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.BeginReply(target);
        vm.Content = "a reply";

        await vm.PostAsync();

        var call = Assert.Single(publisher.Calls);
        Assert.Equal("Reply", call.Method);
        Assert.Same(target, call.Target);
        Assert.Equal("a reply", call.Content);
    }

    [Fact]
    public async Task PostAsync_InQuoteMode_CallsQuoteAsync_WithTheTargetAndContent()
    {
        // Negative control (plan §10.4-3): same reasoning as the Reply test above, for the Quote arm.
        var target = TestNotes.Make(6, 1);
        var publisher = new FakeNotePublisher();
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.BeginQuote(target);
        vm.Content = "a quote";

        await vm.PostAsync();

        var call = Assert.Single(publisher.Calls);
        Assert.Equal("Quote", call.Method);
        Assert.Same(target, call.Target);
        Assert.Equal("a quote", call.Content);
    }

    [Fact]
    public async Task PostAsync_OnSuccessFromReplyMode_ResetsModeAndTargetBackToPost()
    {
        // Negative control (plan §10.4-3): drop the `Mode = ComposeMode.Post; Target = null;` lines from the
        // success continuation and Mode/Target/HasTarget here still read Reply/target/true after the post
        // that was supposed to have been sent and resolved.
        var target = TestNotes.Make(7, 1);
        var publisher = new FakeNotePublisher();
        var sink = new FakePublishedNoteSink();
        var vm = NewViewModel(publisher, sink);
        vm.BeginReply(target);
        vm.Content = "a reply";

        await vm.PostAsync();

        Assert.Equal(ComposeMode.Post, vm.Mode);
        Assert.Null(vm.Target);
        Assert.False(vm.HasTarget);
    }

    /// <summary>Defers every posted action until <see cref="Release"/>, so a test can call <see cref="ComposeViewModel.PostAsync"/>
    /// twice back-to-back before either one's completion continuation runs -- proving the guard is the
    /// synchronous <c>IsBusy</c> check, not a race that happens to resolve in test timing's favor.</summary>
    private sealed class ManualResetPostToUi
    {
        private readonly List<Action> _pending = [];

        public void Post(Action action) => _pending.Add(action);

        public void Release()
        {
            var pending = _pending.ToArray();
            _pending.Clear();
            foreach (var action in pending)
            {
                action();
            }
        }
    }
}
