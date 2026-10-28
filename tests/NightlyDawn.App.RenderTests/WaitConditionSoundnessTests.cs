using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Plan §46/§50: <c>Pump(() =&gt; !X.IsLoading, ...)</c> alone cannot tell "never subscribed" from
/// "finished" -- <see cref="NightlyDawn.App.Timelines.TimelineViewModel.IsLoading"/> defaults to
/// <c>false</c>, the exact value it holds once a subscribe completes, so a tab whose
/// <c>Subscribe()</c> never ran (or never landed -- the original bug was a button click that
/// missed, plan §48) would satisfy that wait immediately, same as a tab that genuinely finished.
/// Every call site in this project now ANDs an extra condition that is only true once
/// <c>Subscribe()</c> has actually left the tab in a terminal state. <c>Status !=
/// TimelineViewModel.IdleStatus</c> is the common case, but it only proves a *first* Subscribe()
/// ran -- Status is never reset, so it is already true before a resubscribe's own wait starts
/// too, degrading back to the same lone-condition bug one level up (PR #50 review). A resubscribe
/// needs either a positive condition true only once that specific attempt lands
/// (<c>AccountWiringRenderTests.cs</c>'s retry uses <c>IsAccountBound</c>) or a <c>StreamCompletion</c>
/// captured right after calling <c>Subscribe()</c>, which is a fresh <see cref="Task"/> instance
/// each call (<c>AvatarRenderTests.cs</c>'s shared helper). This test encodes the mechanical half of
/// that acceptance bar -- scanning source text rather than driving a view model -- so a future call
/// site copy-pasted from an old example cannot silently reintroduce the lone form; the resubscribe
/// distinction above is not something a regex can check and has to stay a review-time judgement.
/// </summary>
public sealed class WaitConditionSoundnessTests
{
    private static readonly Regex LoneIsLoadingWait = new(@"Pump\(\(\)\s*=>\s*!\w+\.IsLoading\s*,", RegexOptions.Compiled);

    [Fact]
    public void NoRenderTestWaitsOnIsLoadingAlone()
    {
        var renderTestsDir = SourceDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(renderTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(renderTestsDir, file);
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
