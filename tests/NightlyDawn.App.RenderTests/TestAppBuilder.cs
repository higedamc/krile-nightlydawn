using Avalonia;
using Avalonia.Headless;
using NightlyDawn.App;
using NightlyDawn.App.RenderTests;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Entry point Avalonia.Headless.XUnit uses to configure the <see cref="Application"/> under test, mirroring
/// <c>NightlyDawn.Host.Program.BuildAvaloniaApp</c>. <c>UseHeadlessDrawing: false</c> is the whole point of this
/// project: a real Skia rasterization pass runs, so <c>CaptureRenderedFrame()</c> returns the actual pixels the
/// layout produced instead of a blank placeholder.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
