using NightlyDawn.App.Theming;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;

namespace NightlyDawn.App.Persistence;

/// <summary>
/// Joins the running layout (<see cref="WorkspaceViewModel"/> + <see cref="ThemeSwitcherViewModel"/>) to an
/// <see cref="IAppStateStore"/> (L9, plan §32): <see cref="Restore"/> once at startup, then a debounced save after
/// every layout change, and one last bounded save when the window closes.
///
/// <para><b>Order of operations is the whole design</b> (plan §32.7). The constructor wires nothing. <see cref="Restore"/>
/// loads, applies, and only <em>then</em> subscribes to <see cref="WorkspaceViewModel.LayoutChanged"/> and the theme
/// switcher -- so the events the restore itself raises are never heard, the built-in default layout is never
/// written to disk, and a first launch leaves no file behind ([[persisted-default-erases-unset]]: a persisted
/// default is indistinguishable from a choice). A state with no columns (<see cref="AppState.Default"/>, i.e. no file
/// or an untrusted one) leaves the built-in layout in place and, again, saves nothing until the user changes something.</para>
///
/// <para><b>Saving is debounced, not per change.</b> Every change takes a snapshot (<see cref="WorkspaceViewModel.CaptureLayout"/>,
/// on the calling thread, so the observable collections are only ever read where they are written) and restarts a
/// timer; when it fires, the newest snapshot is written. Ten keystrokes in a query box are one write.
/// <see cref="FlushAsync"/> writes the pending snapshot now; <see cref="FlushOnClose"/> is the synchronous, bounded form
/// the window's <c>Closed</c> handler uses (plan §32.3: shutdown must not block indefinitely).</para>
///
/// <para><b>Failures are reported, never thrown into the UI.</b> A load that throws (the store contract says it
/// should not for content reasons; this is the backstop for everything else) keeps the default layout. A save that
/// throws -- including <see cref="ArgumentException"/> from a layout past the contract's bounds -- is one line to
/// <c>report</c>; the previous file stays as it was and the next change tries again.</para>
/// </summary>
public sealed class LayoutPersistence : IDisposable
{
    /// <summary>Long enough to swallow a typed query, short enough that an unexpected quit a moment later still
    /// finds the file written. The window's close flush covers the ordinary quit regardless of this value.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(750);

    private readonly IAppStateStore _store;
    private readonly WorkspaceViewModel _workspace;
    private readonly ThemeSwitcherViewModel _theme;
    private readonly Action<string> _report;
    private readonly TimeSpan _debounce;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly object _timerLock = new();

    private AppState? _pending;
    private CancellationTokenSource? _delay;
    private bool _armed;
    private bool _disposed;

    /// <param name="store">Where to read on <see cref="Restore"/> and write afterwards.</param>
    /// <param name="workspace">The layout to restore into and snapshot from. Must already hold its built-in default.</param>
    /// <param name="theme">Supplies <see cref="Settings.Theme"/> for every snapshot and receives the saved one on restore.</param>
    /// <param name="report">One line per problem (load failure, save failure, close-flush timeout). Never called on the happy path.</param>
    /// <param name="debounce">How long after the last change the save runs; <see cref="DefaultDebounce"/> when null. Tests pass a short one.</param>
    public LayoutPersistence(IAppStateStore store, WorkspaceViewModel workspace, ThemeSwitcherViewModel theme, Action<string> report, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(report);
        _store = store;
        _workspace = workspace;
        _theme = theme;
        _report = report;
        _debounce = debounce ?? DefaultDebounce;
    }

    /// <summary>How many saves have reached the store (attempted, whether or not they succeeded). Diagnostic; the
    /// render tests read it to tell "nothing was written because nothing changed" from "the save path is broken".</summary>
    public int SaveAttempts { get; private set; }

    /// <summary>Loads the saved state, applies it, and starts listening for changes. Call exactly once, on the UI
    /// thread, before the window is shown. Synchronous by design -- see <c>MainWindow</c>'s constructor for why
    /// the few milliseconds are preferred over a visible layout swap. Returns true when a saved layout was applied,
    /// false when the built-in default stayed (no file, an untrusted file, a load failure, or a file carrying only
    /// settings -- those are applied regardless of the return value).</summary>
    public bool Restore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_armed)
        {
            throw new InvalidOperationException("Restore has already run.");
        }

        AppState state;
        try
        {
            state = _store.LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // The store contract already turns every content problem into AppState.Default; this catches whatever
            // is left (a store that does not honour that, a cancelled token, an unexpected I/O path) because the
            // one outcome this class must never produce is a window that fails to construct.
            _report($"could not load the saved layout ({ex.GetType().Name}); starting with the defaults");
            state = AppState.Default;
        }

        // Layout and settings are restored independently (L9-2, plan §34): a file with no columns but a theme
        // is a legal state the store accepts, and its theme must not be dropped because the layout half is
        // empty. What is NOT applied is the Default itself -- no file, or an untrusted one -- because there the
        // "System" is nobody's choice and the host's own (NIGHTLYDAWN_THEME) must stand.
        var hasSettings = state.Settings != AppState.Default.Settings;
        if (hasSettings)
        {
            _theme.Selected = ThemeSwitcherViewModel.Options.First(o => o.Value == state.Settings.Theme);
        }

        var restored = false;
        if (state.Columns.Count > 0)
        {
            _workspace.RestoreLayout(state.Columns, state.ActiveColumnId);
            restored = true;
        }

        // Arm only now: nothing above was a user change, and nothing above may be written back.
        _workspace.LayoutChanged += OnLayoutChanged;
        _theme.PropertyChanged += OnThemeChanged;
        _armed = true;
        return restored;
    }

    /// <summary>Writes the pending snapshot, if any, without waiting for the debounce. Safe to call when nothing
    /// is pending (returns promptly) and concurrently with a timer-driven save (the two serialize).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        CancelDelay();
        await WritePendingAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronous <see cref="FlushAsync"/> bounded by <paramref name="timeout"/>, for a <c>Closed</c>
    /// handler that cannot await. Returns false (and reports) when the save did not finish in time -- the
    /// process then exits with the previous file intact; the store's rename-based write guarantees a half-written
    /// file is never what the next launch reads.</summary>
    public bool FlushOnClose(TimeSpan timeout)
    {
        try
        {
            // Not Wait(): the store's awaits all ConfigureAwait(false), so the flush completes on the thread pool
            // without needing the thread that is blocked here. WaitAsync(timeout) gives up cleanly on the deadline
            // rather than leaving the flush half-observed.
            FlushAsync().WaitAsync(timeout).GetAwaiter().GetResult();
            return true;
        }
        catch (TimeoutException)
        {
            _report($"the last save did not finish within {timeout.TotalSeconds:0.#} s; exiting with the previous file");
            return false;
        }
        catch (Exception ex)
        {
            _report($"the last save failed ({ex.GetType().Name}); exiting with the previous file");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_armed)
        {
            _workspace.LayoutChanged -= OnLayoutChanged;
            _theme.PropertyChanged -= OnThemeChanged;
        }

        CancelDelay();
    }

    private void OnLayoutChanged(object? sender, EventArgs e) => ScheduleSave();

    private void OnThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThemeSwitcherViewModel.Selected))
        {
            ScheduleSave();
        }
    }

    private void ScheduleSave()
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _workspace.CaptureLayout(new Settings(Theme: _theme.Selected.Value));
        CancellationToken token;
        lock (_timerLock)
        {
            _pending = snapshot;
            _delay?.Cancel();
            _delay?.Dispose();
            _delay = new CancellationTokenSource();
            token = _delay.Token;
        }

        // Fire-and-forget on purpose: the task's only exit paths are "cancelled" (swallowed inside) and
        // WritePendingAsync, which reports its own failures. Nothing is left to observe.
        _ = SaveAfterDelayAsync(token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Superseded by a newer change, or flushed, or disposed.
        }

        await WritePendingAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppState? state;
            lock (_timerLock)
            {
                state = _pending;
                _pending = null;
            }

            if (state is null)
            {
                return;
            }

            SaveAttempts++;
            try
            {
                await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _report($"could not save the layout ({ex.GetType().Name}: {ex.Message})");
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void CancelDelay()
    {
        lock (_timerLock)
        {
            _delay?.Cancel();
        }
    }
}
