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
