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
/// <c>Subscribe()</c> has actually left the tab in a terminal state (commonly
/// <c>Status != TimelineViewModel.IdleStatus</c>, which no code path ever restores once a subscribe
/// attempt has run). This test encodes that acceptance bar mechanically -- scanning source text
/// rather than driving a view model -- so a future call site copy-pasted from an old example cannot
/// silently reintroduce the lone form.
/// </summary>
public sealed class WaitConditionSoundnessTests
{
    private static readonly Regex LoneIsLoadingWait = new(@"Pump\(\(\)\s*=>\s*!\w+\.IsLoading\s*,", RegexOptions.Compiled);

    [Fact]
    public void NoRenderTestWaitsOnIsLoadingAlone()
    {
        var renderTestsDir = SourceDirectory();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(renderTestsDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (var (line, number) in File.ReadLines(file).Select((text, i) => (text, i + 1)))
            {
                if (LoneIsLoadingWait.IsMatch(line))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static string SourceDirectory([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
}
