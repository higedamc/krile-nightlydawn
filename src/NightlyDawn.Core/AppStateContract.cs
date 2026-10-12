namespace NightlyDawn.Core;

/// <summary>
/// The client's own cross-session state: columns, the tabs in each, which of them are selected, settings. Not
/// the event cache -- that is D5's SQLite, a separate and later concern. One JSON file under the data dir, one
/// atomic write (temp file + rename + fsync; <c>NightlyDawn.Keys/NativeFileSync.cs</c> already has the fsync
/// primitive an implementation should reuse rather than re-deriving it) (plan §1 item 6).
///
/// <para><b>Absence means defaults, and an implementation must not blur that distinction.</b> The first ever
/// <see cref="LoadAsync"/> call, with no file on disk yet, returns <see cref="AppState.Default"/> -- it must
/// not also write that default state to disk as a side effect. If it did, "the user has never touched the
/// theme setting" and "the user explicitly chose <see cref="AppTheme.System"/>" would become
/// indistinguishable the moment a later default changes ([[persisted-default-erases-unset]]). Only
/// <see cref="SaveAsync"/> writes, and only when the caller actually calls it.</para>
///
/// <para><b>The file is untrusted input</b> (plan §32.3). It sits in the user's data directory and can be
/// edited by hand, truncated by a crash, or replaced by anything else that can write there. An implementation
/// must therefore never let <see cref="LoadAsync"/> throw for <em>content</em> reasons: malformed JSON, a
/// <see cref="AppState.SchemaVersion"/> it does not know, or a shape outside the bounds below all resolve to
/// <see cref="AppState.Default"/> (and a diagnostic the host can surface), so a bad file costs the user their
/// layout, never the ability to start the app -- a startup crash loop is unrecoverable for someone who cannot
/// hand-edit JSON. The bounds (<see cref="AppState.MaxColumns"/>, <see cref="AppState.MaxTabsPerColumn"/>,
/// <see cref="AppState.MaxQueryLength"/>, <see cref="AppState.MaxIdLength"/>) are part of this contract so the
/// reader and the writer agree: a state <see cref="SaveAsync"/> would refuse is exactly a state
/// <see cref="LoadAsync"/> would fall back from.</para>
///
/// <para>Declared in 0d; first implemented and consumed in L9 (<c>NightlyDawn.Storage.JsonAppStateStore</c>,
/// restored by <c>MainWindow</c>, saved by <c>App.Persistence.LayoutPersistence</c>).</para>
/// </summary>
public interface IAppStateStore
{
    /// <summary>Returns the persisted state, or <see cref="AppState.Default"/> if nothing has been saved yet or
    /// what is there cannot be trusted (see the type's remarks). Never writes.</summary>
    Task<AppState> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Atomically overwrites the persisted state. Throws <see cref="ArgumentException"/> for a state
    /// outside the contract's bounds (the same bounds <see cref="LoadAsync"/> enforces), rather than writing a
    /// file the next launch would discard.</summary>
    Task SaveAsync(AppState state, CancellationToken cancellationToken = default);
}

/// <param name="SchemaVersion">Written by <see cref="IAppStateStore.SaveAsync"/>; an implementation reads it
/// on <see cref="IAppStateStore.LoadAsync"/> to decide whether a migration is needed, and treats a version it
/// does not know as "cannot be trusted" (fall back to <see cref="Default"/>).
/// <para><b>Still 1 after L9's reshape, deliberately.</b> 0d declared this record as <c>Tabs</c>-outside /
/// columns-inside; L9 (plan §32.1) turned it the right way up (<see cref="Columns"/> outside, tabs inside) to
/// match the UI after plan §21.2's inversion. That reshape did <em>not</em> bump the version and has no
/// migration because no implementation of <see cref="IAppStateStore"/> existed before L9: not one byte of the
/// 0d shape was ever written to anyone's disk, so there is nothing a migration could read. The first leaf that
/// changes this record's shape <em>after</em> L9 has shipped is the first one that needs a version 2 and a
/// migration -- do not read this paragraph as "a migration was forgotten".</para></param>
/// <param name="Columns">Left to right. Empty means "the user has no saved layout" (same as <see cref="Default"/>):
/// the app then builds its built-in default layout at startup and does <em>not</em> save it (plan §32.2 item 5).</param>
/// <param name="ActiveColumnId">The <see cref="Column.Id"/> of the selected column, or null when
/// <paramref name="Columns"/> is empty. Must name one of <paramref name="Columns"/> when non-null.</param>
public sealed record AppState(
    int SchemaVersion,
    IReadOnlyList<Column> Columns,
    string? ActiveColumnId,
    Settings Settings)
{
    /// <summary>The only schema version any implementation has ever written. See the
    /// <see cref="SchemaVersion"/> remarks for why L9's reshape did not bump it.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Upper bound on <see cref="Columns"/> a store will read or write. Far above anything a 1280 DIP
    /// window can show (plan §26.5 budgets three), chosen so a real user never meets it while a hostile file
    /// cannot ask the app to build thousands of columns at startup.</summary>
    public const int MaxColumns = 32;

    /// <summary>Upper bound on <see cref="Column.Tabs"/> per column; same reasoning as <see cref="MaxColumns"/>.
    /// Together with <see cref="MaxColumns"/> and <see cref="MaxQueryLength"/> this also bounds the file a store
    /// must be willing to read back (see <c>JsonAppStateStore.MaxFileBytes</c> for the arithmetic).</summary>
    public const int MaxTabsPerColumn = 32;

    /// <summary>Upper bound on <see cref="Timeline.KqlQuery"/> (and <see cref="Timeline.Title"/>) length in UTF-16
    /// code units. A hand-typed KQL query is tens of characters; a kilobyte-scale one is already unusual.</summary>
    public const int MaxQueryLength = 4096;

    /// <summary>Upper bound on <see cref="Column.Id"/> / <see cref="Timeline.Id"/> length. Ids are
    /// implementation-generated opaque tokens (today a 32-character GUID), never user text.</summary>
    public const int MaxIdLength = 64;

    /// <summary>What <see cref="IAppStateStore.LoadAsync"/> returns before anything has ever been saved: no
    /// columns, no active column, default <see cref="Settings"/>.</summary>
    public static AppState Default { get; } = new(SchemaVersion: CurrentSchemaVersion, Columns: [], ActiveColumnId: null, Settings: new Settings());
}
