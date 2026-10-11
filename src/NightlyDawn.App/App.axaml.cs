using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NightlyDawn.App.Theming;

namespace NightlyDawn.App;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The host (or a test harness) has already put the chosen variant in AppServices; applying it here rather
        // than in App.axaml keeps "follow the OS" (AppTheme.System -> ThemeVariant.Default) expressible.
        ThemeController.Apply(this, AppServices.Theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
