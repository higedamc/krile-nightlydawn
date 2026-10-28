using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace NightlyDawn.App.Tests;

/// <summary>
/// Sibling of <c>NightlyDawn.App.RenderTests.WaitConditionSoundnessTests</c> (plan §46/§50/§59.2):
/// <c>WaitUntil(() =&gt; !X.IsLoading, ...)</c> alone cannot tell "never subscribed" from "finished" --
/// <see cref="NightlyDawn.App.Timelines.TimelineViewModel.IsLoading"/> only becomes <c>true</c> inside
/// <c>StartAsync</c>'s dispatched callback, so a <c>Subscribe()</c> whose callback has not yet run leaves
/// <c>IsLoading</c> at its default <c>false</c> -- the same value it holds once a subscribe completes. A
/// resubscribe needs either a positive condition true only once that specific attempt lands (e.g. a note or
/// row count the attempt itself produced) or a <c>StreamCompletion</c> captured right after calling
/// <c>Subscribe()</c>. <c>Status != TimelineViewModel.IdleStatus</c> does not work here either -- Status is
/// never reset, so it is already true before a resubscribe's own wait starts, degrading back to the same
/// lone-condition bug one level up (PR #50 review). This test encodes the mechanical half of that acceptance
/// bar -- scanning source text rather than driving a view model -- so a future call site copy-pasted from an
/// old example cannot silently reintroduce the lone form; the resubscribe distinction above is not something
/// a regex can check and has to stay a review-time judgement.
/// </summary>
public sealed class WaitConditionSoundnessTests
{
    private static readonly Regex LoneIsLoadingWait = new(@"WaitUntil\(\(\)\s*=>\s*!\w+\.IsLoading\s*,", RegexOptions.Compiled);

    [Fact]
    public void NoAppTestWaitsOnIsLoadingAlone()
    {
        var appTestsDir = SourceDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(appTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(appTestsDir, file);
            if (relative.Split(Path.DirectorySeparatorChar) is ["bin", ..] or ["obj", ..])
            {
                continue; // build output, not source.
            }

            foreach (var (line, number) in File.ReadLines(file).Select((text, i) => (text, i + 1)))
            {
                if (LoneIsLoadingWait.IsMatch(line))
                {
                    offenders.Add($"{relative}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static string SourceDirectory([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
}
