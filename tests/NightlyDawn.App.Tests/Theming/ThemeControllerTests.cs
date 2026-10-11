using Avalonia.Styling;
using NightlyDawn.App.Theming;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.Tests.Theming;

/// <summary>
/// The parsing and mapping half of L1's theme switch. Applying a variant to a live <see cref="Avalonia.Application"/>
/// is asserted on pixels instead, in <c>NightlyDawn.App.RenderTests.ThemeRenderTests</c>: a mapping test can only
/// show the intent, not that the colour reached the screen.
/// </summary>
public sealed class ThemeControllerTests
{
    [Theory]
    [InlineData("light", AppTheme.Light)]
    [InlineData("LIGHT", AppTheme.Light)]
    [InlineData("  Dark  ", AppTheme.Dark)]
    [InlineData("system", AppTheme.System)]
    [InlineData("default", AppTheme.System)]
    public void TryParse_RecognisedValues_Succeed(string value, AppTheme expected)
    {
        Assert.True(ThemeController.TryParse(value, out var theme));
        Assert.Equal(expected, theme);
    }

    /// <summary>Unset is a valid state, not a failure: the overwhelmingly common case is "the variable is not set".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParse_Unset_IsSystemAndSucceeds(string? value)
    {
        Assert.True(ThemeController.TryParse(value, out var theme));
        Assert.Equal(AppTheme.System, theme);
    }

    /// <summary>A typo must be distinguishable from "system", or the host cannot say anything about it and a
    /// misspelled variable looks exactly like a working one. The out value still has to be safe to use.</summary>
    [Theory]
    [InlineData("drak")]
    [InlineData("night")]
    [InlineData("true")]
    public void TryParse_Unrecognised_FailsButStillYieldsSystem(string value)
    {
        Assert.False(ThemeController.TryParse(value, out var theme));
        Assert.Equal(AppTheme.System, theme);
    }

    /// <summary>System maps to Default (follow the OS), not to a pinned variant. Collapsing those two would make
    /// "never chosen" indistinguishable from "chose light" the moment a default changes.</summary>
    [Fact]
    public void ToVariant_MapsSystemToDefault_NotToAPinnedVariant()
    {
        Assert.Equal(ThemeVariant.Default, ThemeController.ToVariant(AppTheme.System));
        Assert.Equal(ThemeVariant.Light, ThemeController.ToVariant(AppTheme.Light));
        Assert.Equal(ThemeVariant.Dark, ThemeController.ToVariant(AppTheme.Dark));
    }
}
