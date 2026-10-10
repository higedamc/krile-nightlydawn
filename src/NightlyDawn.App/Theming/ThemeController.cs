using Avalonia;
using Avalonia.Styling;
using NightlyDawn.Core;

namespace NightlyDawn.App.Theming;

/// <summary>
/// Maps Core's <see cref="AppTheme"/> onto Avalonia's <see cref="ThemeVariant"/> and applies it to a running
/// <see cref="Application"/> (L1). The one place the theme is switched, so the render harness and the host use
/// the same code path the app does -- a switch only the host could reach would be a switch no test ever exercises.
///
/// <para><see cref="AppTheme.System"/> maps to <see cref="ThemeVariant.Default"/>, which follows the OS setting
/// rather than pinning a variant. That distinction is load-bearing: "the user never chose" must not collapse into
/// "the user chose light" ([[persisted-default-erases-unset]], and the same reasoning
/// <c>NightlyDawn.Core/AppStateContract.cs</c> spells out for the persisted state this will eventually read from).</para>
/// </summary>
public static class ThemeController
{
    /// <summary>Environment variable the host reads to pin a variant: <c>light</c>, <c>dark</c> or <c>system</c>.</summary>
    public const string EnvironmentVariable = "NIGHTLYDAWN_THEME";

    public static ThemeVariant ToVariant(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>
    /// Parses <see cref="EnvironmentVariable"/>. Unset or whitespace is <see cref="AppTheme.System"/> and succeeds
    /// (no setting is a valid state, not an error). Anything unrecognised fails with <paramref name="theme"/> left at
    /// <see cref="AppTheme.System"/>, so a caller can say so out loud instead of silently rendering a variant the
    /// user did not ask for -- a typo'd variable that quietly means "system" is indistinguishable from a working one.
    /// </summary>
    public static bool TryParse(string? value, out AppTheme theme)
    {
        theme = AppTheme.System;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "system":
            case "default":
                return true;
            case "light":
                theme = AppTheme.Light;
                return true;
            case "dark":
                theme = AppTheme.Dark;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Applies <paramref name="theme"/> to <paramref name="application"/>. Safe to call before any window
    /// exists (the host's path) and after (the render harness's path, and a future in-app toggle).</summary>
    public static void Apply(Application application, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.RequestedThemeVariant = ToVariant(theme);
    }
}
