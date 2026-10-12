using NightlyDawn.Core;

namespace NightlyDawn.Storage;

/// <summary>
/// The one definition of "an <see cref="AppState"/> this store will read or write" (plan §32.3's bounds, plus
/// the referential rules the contract's parameter docs state). <see cref="JsonAppStateStore.LoadAsync"/> runs it
/// on what the file said and falls back to <see cref="AppState.Default"/> on a problem;
/// <see cref="JsonAppStateStore.SaveAsync"/> runs it on what the app wants to write and throws instead -- the
/// same function on both sides is what makes the contract's "a state SaveAsync would refuse is exactly a state
/// LoadAsync would fall back from" true by construction rather than by two lists kept in step by hand.
/// </summary>
internal static class AppStateShape
{
    /// <summary>Returns the first problem found, phrased for a diagnostic, or null when the state is in-bounds.
    /// Never quotes the state's own text (ids and queries) -- only counts, lengths and positions -- so the message
    /// is safe to log whatever the file contained.</summary>
    public static string? Check(AppState state)
    {
        if (state.SchemaVersion != AppState.CurrentSchemaVersion)
        {
            return $"schemaVersion {state.SchemaVersion} is not {AppState.CurrentSchemaVersion}";
        }

        if (state.Columns.Count > AppState.MaxColumns)
        {
            return $"{state.Columns.Count} columns is above the limit of {AppState.MaxColumns}";
        }

        var columnIds = new HashSet<string>(StringComparer.Ordinal);
        for (var c = 0; c < state.Columns.Count; c++)
        {
            var column = state.Columns[c];
            if (!IsValidId(column.Id))
            {
                return $"columns[{c}].id is empty or longer than {AppState.MaxIdLength}";
            }

            if (!columnIds.Add(column.Id))
            {
                return $"columns[{c}].id repeats an earlier column's id";
            }

            if (column.Tabs.Count > AppState.MaxTabsPerColumn)
            {
                return $"columns[{c}] has {column.Tabs.Count} tabs, above the limit of {AppState.MaxTabsPerColumn}";
            }

            var tabIds = new HashSet<string>(StringComparer.Ordinal);
            for (var t = 0; t < column.Tabs.Count; t++)
            {
                var tab = column.Tabs[t];
                if (!IsValidId(tab.Id))
                {
                    return $"columns[{c}].tabs[{t}].id is empty or longer than {AppState.MaxIdLength}";
                }

                if (!tabIds.Add(tab.Id))
                {
                    return $"columns[{c}].tabs[{t}].id repeats an earlier tab's id in the same column";
                }

                if (tab.Title.Length > AppState.MaxQueryLength)
                {
                    return $"columns[{c}].tabs[{t}].title is {tab.Title.Length} characters, above the limit of {AppState.MaxQueryLength}";
                }

                if (tab.KqlQuery.Length > AppState.MaxQueryLength)
                {
                    return $"columns[{c}].tabs[{t}].kqlQuery is {tab.KqlQuery.Length} characters, above the limit of {AppState.MaxQueryLength}";
                }
            }

            // Core/Entities.cs, Column.SelectedTabId: "null when Tabs is empty; must name one of Tabs when non-null".
            // The running ColumnViewModel never has tabs without a selection either, so the stricter "null if and
            // only if empty" is what a file this store wrote always satisfies.
            if (column.Tabs.Count == 0 ? column.SelectedTabId is not null : column.SelectedTabId is null || !tabIds.Contains(column.SelectedTabId))
            {
                return $"columns[{c}].selectedTabId does not name one of that column's tabs";
            }
        }

        if (state.Columns.Count == 0 ? state.ActiveColumnId is not null : state.ActiveColumnId is null || !columnIds.Contains(state.ActiveColumnId))
        {
            return "activeColumnId does not name one of the columns";
        }

        if (!Enum.IsDefined(state.Settings.Theme))
        {
            return "settings.theme is not a known theme";
        }

        return null;
    }

    private static bool IsValidId(string id) => id.Length is > 0 and <= AppState.MaxIdLength;
}
