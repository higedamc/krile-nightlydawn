using System.ComponentModel;
using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.Theming;

/// <summary>
/// The in-app theme switch (L8). Until this leaf the variant could only be chosen with
/// <c>NIGHTLYDAWN_THEME</c> before launch, which means the only way to see the other variant was to quit the app.
///
/// <para>Three options, not a two-state toggle: <see cref="AppTheme.System"/> has to stay reachable and
/// distinguishable from a pinned variant, or "the user never chose" collapses into "the user chose light" the first
/// time anyone touches the control (<c>Core/AppStateContract.cs</c> and
/// <see cref="ThemeController.ToVariant"/> make the same point).</para>
///
/// <para><b>Deliberately not persisted.</b> A choice made here lasts until the process exits; the next launch is
/// back to whatever the host passed in. Writing the choice to disk is the layout-persistence leaf's job (L9,
/// <see cref="Core.IAppStateStore"/>), and doing it here would mean inventing a second store for one enum.</para>
///
/// <para><b>Who owns "the current theme".</b> The running <see cref="Avalonia.Application"/> does -- its
/// <c>RequestedThemeVariant</c> is what the screen reflects. <see cref="AppServices.Theme"/> stays what it says it
/// is: the value the host chose at startup, read once by <c>App.Initialize</c>. This view model seeds
/// <see cref="Selected"/> from it and never writes it back, so nothing has to keep two mutable copies of the same
/// state in step.</para>
/// </summary>
public sealed class ThemeSwitcherViewModel : INotifyPropertyChanged
{
    private readonly Action<AppTheme> _apply;
    private ThemeOption _selected;

    /// <summary>
    /// </summary>
    /// <param name="initial">The variant the app started in; normally <see cref="AppServices.Theme"/>.</param>
    /// <param name="apply">Applies a chosen variant. Production passes
    /// <c>theme => ThemeController.Apply(Application.Current!, theme)</c>; a test can pass a recorder.</param>
    public ThemeSwitcherViewModel(AppTheme initial, Action<AppTheme> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _apply = apply;

        // Options covers every declared AppTheme by construction (see below), so there is always a match.
        _selected = Options.First(o => o.Value == initial);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The choices, in <see cref="AppTheme"/>'s declaration order (System first, so "follow the OS" reads as the
    /// neutral default it is).
    ///
    /// <para>Derived from the enum rather than hand-listed on purpose: a hand-written list that someone forgets to
    /// extend leaves a theme with no option to represent it, and then the constructor below either throws while
    /// building the main window (a cosmetic setting that stops the app from starting) or silently shows the wrong
    /// current state. Deriving it makes that case unreachable instead of handled.</para>
    /// </summary>
    public static IReadOnlyList<ThemeOption> Options { get; } =
        Enum.GetValues<AppTheme>().Select(theme => new ThemeOption(theme, theme.ToString())).ToArray();

    /// <summary>
    /// The chosen option. Setting it applies the variant immediately -- that call is the entire feature, so the
    /// required negative control for this leaf is deleting it: the rendered picture must then stop changing when
    /// the drop-down is used.
    /// </summary>
    public ThemeOption Selected
    {
        get => _selected;
        set
        {
            // A ComboBox clears SelectedItem while its items are being rebuilt. Treating that as a choice
            // would apply a variant nobody picked, so null is ignored rather than mapped onto a default.
            if (value is null || ReferenceEquals(value, _selected))
            {
                return;
            }

            _selected = value;
            OnPropertyChanged();
            _apply(value.Value);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>One entry in the theme drop-down: the Core value and the label shown for it.</summary>
/// <param name="Value">The Core theme this option selects.</param>
/// <param name="Label">What the drop-down shows. English, like the rest of the UI.</param>
public sealed record ThemeOption(AppTheme Value, string Label);
