using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>PROBE, never to be merged: fails on purpose to print 3-column widths (plan §25.3 method).</summary>
public sealed class ProbeColumnWidthTests
{
    [AvaloniaFact]
    public void Probe_ThreeColumnsAt1280x800()
    {
        var tempDir = Directory.CreateTempSubdirectory("nightlydawn-probe").FullName;
        AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(tempDir, "key.ncryptsec")));
        AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
        AppServices.ProfileStore = null;
        AppServices.ScreenshotPath = null;

        var window = new MainWindow { Width = 1280, Height = 800 };
        window.Show();
        HeadlessWindowExtensions.SetRenderScaling(window, 1.0);
        Dispatcher.UIThread.RunJobs();

        var workspace = (WorkspaceViewModel)window.DataContext!;
        workspace.AddColumn();
        workspace.AddColumn();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();

        var host = window.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "ColumnsHost");
        var lists = host.GetVisualDescendants().OfType<ListBox>().Where(l => l.Name == "TimelineList").ToList();
        var columns = host.GetVisualDescendants().OfType<ContentPresenter>().Where(p => p.DataContext is ColumnViewModel).ToList();

        var message = $"PROBE columns={workspace.Columns.Count} ColumnsHost={host.Bounds.Width:0.#}x{host.Bounds.Height:0.#} "
                    + $"columnWidths=[{string.Join(", ", columns.Select(c => c.Bounds.Width.ToString("0.#")))}] "
                    + $"TimelineList=[{string.Join(", ", lists.Select(l => $"{l.Bounds.Width:0.#}x{l.Bounds.Height:0.#}"))}]";
        window.Close();
        throw new Xunit.Sdk.XunitException(message);
    }
}
