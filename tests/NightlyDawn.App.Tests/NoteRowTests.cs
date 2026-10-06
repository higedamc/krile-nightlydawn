using NightlyDawn.App.Tests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests;

public class NoteRowTests
{
    [Fact]
    public void From_ShortensAuthor_AndKeepsContentVerbatim()
    {
        var row = NoteRow.From(TestNotes.Make(1, createdAt: 1_700_000_000, content: "hello <b>world</b> & co"));

        Assert.Equal("abcdef01…", row.AuthorLabel);
        Assert.Equal("hello <b>world</b> & co", row.DisplayContent); // Rendered by a TextBlock, so markup stays inert text.
    }

    [Fact]
    public void From_TruncatesOversizedContent()
    {
        var huge = new string('x', NoteRow.MaxContentChars * 20);

        var row = NoteRow.From(TestNotes.Make(1, createdAt: 1, content: huge));

        Assert.Equal(NoteRow.MaxContentChars + 1, row.DisplayContent.Length);
        Assert.EndsWith("…", row.DisplayContent, StringComparison.Ordinal);
    }

    [Fact]
    public void From_LabelsEmptyReposts()
    {
        Assert.Equal("(repost)", NoteRow.From(TestNotes.Make(1, 1, content: "", kind: NoteKind.Repost)).DisplayContent);
        Assert.Equal("(generic repost)", NoteRow.From(TestNotes.Make(2, 1, content: "", kind: NoteKind.GenericRepost)).DisplayContent);
        Assert.Equal("", NoteRow.From(TestNotes.Make(3, 1, content: "", kind: NoteKind.Text)).DisplayContent);
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1)]
    [InlineData(253_402_300_800)] // One second past DateTimeOffset.MaxValue.
    public void FormatTime_DoesNotThrowOnOutOfRangeTimestamps(long createdAt)
    {
        Assert.Equal("?", NoteRow.FormatTime(createdAt, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FormatTime_UsesTimeOnlyForToday_AndDateOtherwise()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var todayLocal = now.ToLocalTime();
        var sameDay = new DateTimeOffset(todayLocal.Year, todayLocal.Month, todayLocal.Day, 9, 30, 0, todayLocal.Offset);

        Assert.Equal("09:30", NoteRow.FormatTime(sameDay.ToUnixTimeSeconds(), now));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", NoteRow.FormatTime(now.AddDays(-3).ToUnixTimeSeconds(), now));
    }
}
