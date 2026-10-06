using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

public class SampleTimelineSourceFactoryTests
{
    private static readonly Timeline Timeline = new("t1", "from home", "from home");

    [Fact]
    public async Task Anonymous_EmitsBacklog_ThenInitialLoadComplete_ThenOneLiveNote_AllLabelledSample()
    {
        var factory = new SampleTimelineSourceFactory(liveNoteDelay: TimeSpan.Zero);
        var updates = new List<TimelineUpdate>();

        await foreach (var update in factory.CreateAnonymous(Timeline).StreamAsync())
        {
            updates.Add(update);
        }

        var backlog = SampleTimelineSourceFactory.SampleTimelineSource.BacklogCount;
        Assert.Equal(backlog + 2, updates.Count);
        Assert.All(updates.Take(backlog), u => Assert.IsType<NoteArrived>(u));
        Assert.IsType<InitialLoadComplete>(updates[backlog]);
        Assert.IsType<NoteArrived>(updates[^1]);

        var notes = updates.OfType<NoteArrived>().Select(n => n.Note).ToList();
        Assert.All(notes, n => Assert.StartsWith("[sample]", n.Content, StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Content.Contains(Timeline.KqlQuery, StringComparison.Ordinal));
        Assert.Equal(notes.Count, notes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(notes.Zip(notes.Skip(1)).All(pair => pair.First.CreatedAt <= pair.Second.CreatedAt)); // Backlog oldest-first, live last.
    }

    [Fact]
    public async Task Create_WithAccount_IsTheSameSampleStream()
    {
        var factory = new SampleTimelineSourceFactory(liveNoteDelay: TimeSpan.Zero);
        var account = new Account("00", new SignerDescriptor("00", SignerKind.Nip46));

        var count = 0;
        await foreach (var _ in factory.Create(Timeline, account).StreamAsync())
        {
            count++;
        }

        Assert.Equal(SampleTimelineSourceFactory.SampleTimelineSource.BacklogCount + 2, count);
    }

    [Fact]
    public async Task Stream_HonoursCancellation_WhileWaitingForTheLiveNote()
    {
        var factory = new SampleTimelineSourceFactory(liveNoteDelay: TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var update in factory.CreateAnonymous(Timeline).StreamAsync(cts.Token))
            {
                seen++;
                if (update is InitialLoadComplete)
                {
                    cts.Cancel();
                }
            }
        });

        Assert.Equal(SampleTimelineSourceFactory.SampleTimelineSource.BacklogCount + 1, seen);
    }

    [Fact]
    public async Task LoadOlder_ReturnsNothing()
    {
        var source = new SampleTimelineSourceFactory().CreateAnonymous(Timeline);

        Assert.Empty(await source.LoadOlderAsync(beforeCreatedAt: long.MaxValue, limit: 10));
    }
}
