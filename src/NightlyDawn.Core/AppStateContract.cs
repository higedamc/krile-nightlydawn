namespace NightlyDawn.Core;

/// <summary>
/// The client's own cross-session state: tabs, which tab is active, settings. Not the event cache -- that is
/// D5's SQLite, a separate and later concern. One JSON file under the data dir, one atomic write (temp file +
/// rename + fsync; <c>NightlyDawn.Keys/NativeFileSync.cs</c> already has the fsync primitive an
/// implementation should reuse rather than re-deriving it) (plan §1 item 6).
///
/// <para><b>Absence means defaults, and an implementation must not blur that distinction.</b> The first ever
/// <see cref="LoadAsync"/> call, with no file on disk yet, returns <see cref="AppState.Default"/> -- it must
/// not also write that default state to disk as a side effect. If it did, "the user has never touched the
/// theme setting" and "the user explicitly chose <see cref="AppTheme.System"/>" would become
/// indistinguishable the moment a later default changes ([[persisted-default-erases-unset]]). Only
/// <see cref="SaveAsync"/> writes, and only when the caller actually calls it.</para>
///
/// <para>Declaration only (0d leaf brief): no implementation here.</para>
/// </summary>
public interface IAppStateStore
{
    /// <summary>Returns the persisted state, or <see cref="AppState.Default"/> if nothing has been saved yet. Never writes.</summary>
    Task<AppState> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically overwrites the persisted state.</summary>
    Task SaveAsync(AppState state, CancellationToken cancellationToken = default);
}

/// <param name="SchemaVersion">Written by <see cref="IAppStateStore.SaveAsync"/>; an implementation reads it
/// on <see cref="IAppStateStore.LoadAsync"/> to decide whether a migration is needed. 0d does not define what
/// a migration looks like -- the first leaf that changes this record's shape does.</param>
public sealed record AppState(
    int SchemaVersion,
    IReadOnlyList<Tab> Tabs,
    string? ActiveTabId,
    Settings Settings)
{
    /// <summary>What <see cref="IAppStateStore.LoadAsync"/> returns before anything has ever been saved: no
    /// tabs, no active tab, default <see cref="Settings"/>.</summary>
    public static AppState Default { get; } = new(SchemaVersion: 1, Tabs: [], ActiveTabId: null, Settings: new Settings());
}
