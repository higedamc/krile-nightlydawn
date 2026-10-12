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
    [InlineData(NoteKind.Repost, false)]
    [InlineData(NoteKind.GenericRepost, false)]
    [InlineData(NoteKind.Text, true)]
    public void CanReplyOrRepost_IsFalseForRepostKinds(NoteKind kind, bool expected)
    {
        // Negative control (plan §10.3 ④): the repost/generic-repost cases above are what NoteRowView binds
        // Reply/Repost's IsEnabled to -- flipping either literal here to true would make this red.
        var row = NoteRow.From(TestNotes.Make(1, 1, kind: kind));

        Assert.Equal(expected, row.CanReplyOrRepost);
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

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    [InlineData(-1)]
    [InlineData(253_402_300_800)]
    public void FormatAbsoluteTime_DoesNotThrowOnOutOfRangeTimestamps(long createdAt)
    {
        Assert.Equal("?", NoteRow.FormatAbsoluteTime(createdAt));
    }

    /// <summary>The hover label (plan §21.3) is the one place the date and the seconds are shown, so it keeps
    /// them for today's notes too -- which is exactly where <see cref="NoteRow.FormatTime"/> drops them.</summary>
    [Fact]
    public void FormatAbsoluteTime_KeepsTheDateAndSeconds_EvenForToday()
    {
        var todayLocal = DateTimeOffset.Now;
        var sameDay = new DateTimeOffset(todayLocal.Year, todayLocal.Month, todayLocal.Day, 9, 30, 7, todayLocal.Offset);

        Assert.Equal(sameDay.ToString("yyyy-MM-dd HH:mm:ss"), NoteRow.FormatAbsoluteTime(sameDay.ToUnixTimeSeconds()));
        Assert.Equal("09:30", NoteRow.FormatTime(sameDay.ToUnixTimeSeconds(), todayLocal));
    }

    /// <summary>The property the row binds to, not just the helper behind it: a <c>=> TimeLabel</c> typo would
    /// leave the hover swap showing the same lossy string twice and no render test would notice.</summary>
    [Fact]
    public void AbsoluteTimeLabel_IsTheAbsoluteForm_NotTheShortOne()
    {
        var row = NoteRow.From(TestNotes.Make(1, 1_700_000_000));

        Assert.Equal(NoteRow.FormatAbsoluteTime(1_700_000_000), row.AbsoluteTimeLabel);
        Assert.NotEqual(row.TimeLabel, row.AbsoluteTimeLabel);
    }
}
