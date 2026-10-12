using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightlyDawn.Core;
using NightlyDawn.Keys;

namespace NightlyDawn.Storage;

/// <summary>
/// <see cref="IAppStateStore"/> over one JSON file (L9, plan §32.2): the column/tab layout, the selection, and
/// <see cref="Settings.Theme"/>, written atomically and read as untrusted input.
///
/// <para><b>Reading never throws for what is in the file</b> (plan §32.3; the contract's remarks explain why a
/// startup crash loop is the one failure a user cannot recover from). A missing file is the ordinary first
/// launch and returns <see cref="AppState.Default"/> silently. Anything else that is not a well-formed,
/// in-bounds, schema-version-1 document -- malformed JSON, the wrong root type, an unknown
/// <see cref="AppState.SchemaVersion"/>, a null where the shape needs a value, an unknown theme name, more
/// columns/tabs or longer text than <see cref="AppState"/>'s bounds allow, a selection id that names nothing,
/// a file larger than <see cref="MaxFileBytes"/>, or an I/O failure other than "not there" -- also returns
/// <see cref="AppState.Default"/>, but says why through the <c>reportProblem</c> callback so the host can put
/// one line on stderr. The user loses their layout, never the ability to open the app. The file on disk is left
/// exactly as it was: this class never repairs, truncates, or deletes it.</para>
///
/// <para><b>Writing is temp file + fsync + rename + directory fsync</b>, the same shape and the same
/// <see cref="NativeFileSync"/> primitive as <c>NightlyDawn.Keys.FileKeyFileStore</c> (and for the same reason:
/// <see cref="FileStream.Flush(bool)"/> silently reports a failed fsync as success, dotnet/runtime#135201).
/// A reader therefore always sees either the previous complete file or the new complete file. The file is
/// created 0600 like the key file beside it: the layout is a record of what the user reads.</para>
///
/// <para><b>Nothing secret is ever in the document.</b> The only inputs are an <see cref="AppState"/>'s four
/// fields; there is no path by which key material, a passphrase, or a signer handle could reach the
/// serializer, and <c>LayoutPersistenceRenderTests</c> pins that with a real key store beside a real save.</para>
///
/// <para>The diagnostics handed to <c>reportProblem</c> describe the problem (where in the file, which bound)
/// and never quote the file's content, so a hostile file cannot smuggle text onto the operator's terminal
/// through this class (the host still filters control characters, CWE-117).</para>
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class JsonAppStateStore : IAppStateStore
{
    /// <summary>Hard ceiling on the bytes <see cref="LoadAsync"/> will read before parsing, so a file that is
    /// not ours cannot ask for an unbounded allocation at startup. Derived from the contract's bounds so that
    /// every state <see cref="SaveAsync"/> accepts is also readable: <see cref="AppState.MaxColumns"/> ×
    /// <see cref="AppState.MaxTabsPerColumn"/> = 1,024 tabs, each carrying two strings of at most
    /// <see cref="AppState.MaxQueryLength"/> UTF-16 units that the default encoder escapes to at most 6 bytes
    /// each (<c>\uXXXX</c>), is 1,024 × 2 × 4,096 × 6 ≈ 48 MiB; ids, member names and indentation add well
    /// under 1 MiB on top. 64 MiB clears that worst case with room; a real layout is a few kilobytes.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true, // The file is meant to be readable by a person, not only by this class.
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _filePath;
    private readonly Action<string>? _reportProblem;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <param name="filePath">The one JSON file. Its directory is created on the first save, not before.</param>
    /// <param name="reportProblem">Receives one line per fallback-to-default decision (see the class remarks).
    /// Null drops the diagnostics; the behaviour is identical either way.</param>
    public JsonAppStateStore(string filePath, Action<string>? reportProblem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        _reportProblem = reportProblem;
    }

    /// <summary>Where this store reads and writes. Exposed so a host can log it.</summary>
    public string FilePath => _filePath;

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            if (Directory.Exists(_filePath))
            {
                return Fallback("the path is a directory, not a file"); // FileInfo.Exists would say "absent", which would be a lie.
            }

            var info = new FileInfo(_filePath);
            if (!info.Exists)
            {
                return AppState.Default; // First launch (or the user deleted it): the ordinary case, no diagnostic.
            }

            if (info.Length > MaxFileBytes)
            {
                return Fallback($"file is {info.Length} bytes, above the {MaxFileBytes}-byte limit");
            }

            bytes = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return AppState.Default; // Raced with a delete between Exists and the read: still "not there".
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fallback($"could not read the file ({ex.GetType().Name})");
        }

        if (bytes.Length > MaxFileBytes)
        {
            return Fallback($"file is {bytes.Length} bytes, above the {MaxFileBytes}-byte limit"); // Grew between the length check and the read.
        }

        StateDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<StateDocument>(bytes, SerializerOptions);
        }
        catch (JsonException ex)
        {
            // Path/line/position only: JsonException.Message can quote a token from the file, and nothing from
            // the file should reach a log line verbatim.
            return Fallback($"malformed JSON at line {ex.LineNumber ?? -1}, byte {ex.BytePositionInLine ?? -1} (path {ex.Path ?? "$"})");
        }

        if (document is null)
        {
            return Fallback("the document is the JSON literal null");
        }

        var problem = TryMaterialize(document, out var state);
        return problem is null ? state! : Fallback(problem);
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var problem = AppStateShape.Check(state);
        if (problem is not null)
        {
            throw new ArgumentException($"Refusing to write a state the next launch would discard: {problem}.", nameof(state));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(StateDocument.From(state), SerializerOptions);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Same sequence as <c>FileKeyFileStore.WriteAsync</c>; see that class for the failure-mode-by-
    /// failure-mode account of what the fsyncs buy. Kept as a copy of the <em>sequence</em> rather than a shared
    /// helper because the key file's class doc is load-bearing for the key file and this leaf does not touch
    /// <c>NightlyDawn.Keys</c> beyond making <see cref="NativeFileSync"/> visible.</summary>
    private async Task WriteAtomicallyAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath); // Left over from a write that never reached the rename.
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        await using (var stream = new FileStream(tempPath, options))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            NativeFileSync.SyncToDisk(stream.SafeFileHandle);
        }

        File.Move(tempPath, _filePath, overwrite: true);

        if (!string.IsNullOrEmpty(directory))
        {
            NativeFileSync.SyncDirectoryToDisk(directory);
        }
    }

    private AppState Fallback(string problem)
    {
        _reportProblem?.Invoke($"ignoring the saved layout and starting with the defaults: {problem}");
        return AppState.Default;
    }

    /// <summary>Turns the loosely-typed document into an <see cref="AppState"/>, or names the first reason it
    /// cannot. Every null check here is a shape the serializer accepts but the contract does not.</summary>
    private static string? TryMaterialize(StateDocument document, out AppState? state)
    {
        state = null;

        if (document.SchemaVersion != AppState.CurrentSchemaVersion)
        {
            return $"schemaVersion {document.SchemaVersion?.ToString() ?? "(missing)"} is not {AppState.CurrentSchemaVersion}";
        }

        if (document.Columns is null)
        {
            return "columns is missing";
        }

        if (document.Columns.Count > AppState.MaxColumns)
        {
            return $"{document.Columns.Count} columns is above the limit of {AppState.MaxColumns}"; // Before building anything.
        }

        var columns = new List<Column>(document.Columns.Count);
        for (var c = 0; c < document.Columns.Count; c++)
        {
            var column = document.Columns[c];
            if (column is null || column.Id is null || column.Tabs is null)
            {
                return $"columns[{c}] is missing id or tabs";
            }

            if (column.Tabs.Count > AppState.MaxTabsPerColumn)
            {
                return $"columns[{c}] has {column.Tabs.Count} tabs, above the limit of {AppState.MaxTabsPerColumn}";
            }

            var tabs = new List<Timeline>(column.Tabs.Count);
            for (var t = 0; t < column.Tabs.Count; t++)
            {
                var tab = column.Tabs[t];
                if (tab is null || tab.Id is null || tab.Title is null || tab.KqlQuery is null)
                {
                    return $"columns[{c}].tabs[{t}] is missing id, title or kqlQuery";
                }

                tabs.Add(new Timeline(tab.Id, tab.Title, tab.KqlQuery));
            }

            columns.Add(new Column(column.Id, tabs, column.SelectedTabId));
        }

        if (document.Settings is null)
        {
            return "settings is missing";
        }

        if (document.Settings.Theme is null || !Enum.TryParse<AppTheme>(document.Settings.Theme, ignoreCase: false, out var theme) || !Enum.IsDefined(theme))
        {
            return "settings.theme is not one of System, Light, Dark";
        }

        var candidate = new AppState(AppState.CurrentSchemaVersion, columns, document.ActiveColumnId, new Settings(Theme: theme));
        var problem = AppStateShape.Check(candidate);
        if (problem is not null)
        {
            return problem;
        }

        state = candidate;
        return null;
    }

    /// <summary>The on-disk shape. Nullable everywhere so a missing or null member is <em>our</em> decision
    /// (<see cref="TryMaterialize"/>) rather than a serializer exception, and a class rather than the Core
    /// records so the serializer never constructs a <see cref="Column"/> or <see cref="Timeline"/> that the
    /// contract's bounds have not seen yet. Unknown members are ignored on read (a newer build's file may carry
    /// more); members are written in this declaration order.</summary>
    private sealed class StateDocument
    {
        public int? SchemaVersion { get; set; }

        public List<ColumnDocument?>? Columns { get; set; }

        public string? ActiveColumnId { get; set; }

        public SettingsDocument? Settings { get; set; }

        public static StateDocument From(AppState state) => new()
        {
            SchemaVersion = state.SchemaVersion,
            Columns = state.Columns.Select(c => (ColumnDocument?)new ColumnDocument
            {
                Id = c.Id,
                Tabs = c.Tabs.Select(t => (TabDocument?)new TabDocument { Id = t.Id, Title = t.Title, KqlQuery = t.KqlQuery }).ToList(),
                SelectedTabId = c.SelectedTabId,
            }).ToList(),
            ActiveColumnId = state.ActiveColumnId,
            // Theme only (plan §32.2 item 4). Settings.Locale and Settings.Extra have no UI and no reader yet;
            // writing them would persist a default nobody chose ([[persisted-default-erases-unset]]).
            Settings = new SettingsDocument { Theme = state.Settings.Theme.ToString() },
        };
    }

    private sealed class ColumnDocument
    {
        public string? Id { get; set; }

        public List<TabDocument?>? Tabs { get; set; }

        public string? SelectedTabId { get; set; }
    }

    private sealed class TabDocument
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? KqlQuery { get; set; }
    }

    private sealed class SettingsDocument
    {
        public string? Theme { get; set; }
    }
}
