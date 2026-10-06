using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Relay;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// The relay-backed <see cref="ITimelineSourceFactory"/> (leaf 1e-b). Owns (or borrows) one <see cref="NostrBackend"/>,
/// connects it lazily on the first stream, and compiles each column's query through <see cref="ITimelineQueryCompiler"/>.
/// Read-only: nothing here signs or publishes. <see cref="Create"/> currently ignores the account — account-bound
/// sources (home = follows, mentions) arrive with the key store and the 1f wiring.
/// </summary>
public sealed class NostrTimelineSourceFactory : ITimelineSourceFactory, IAsyncDisposable
{
    private readonly NostrBackend _backend;
    private readonly bool _ownsBackend;
    private readonly IReadOnlyCollection<RelayUrl> _relays;
    private readonly ITimelineQueryCompiler _compiler;
    private readonly TimeSpan _initialLoadTimeout;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private volatile bool _connected;

    /// <param name="relays">Relays to read from. At least one; <c>wss://</c> unless <paramref name="options"/> opts into <c>ws://</c>.</param>
    public NostrTimelineSourceFactory(IReadOnlyCollection<RelayUrl> relays, NostrBackendOptions? options = null, ILoggerFactory? loggerFactory = null)
        : this(
            new NostrBackend(options, loggerFactory?.CreateLogger<NostrBackend>()),
            relays,
            compiler: null,
            initialLoadTimeout: null,
            ownsBackend: true,
            logger: loggerFactory?.CreateLogger<NostrTimelineSourceFactory>())
    {
    }

    internal NostrTimelineSourceFactory(
        NostrBackend backend,
        IReadOnlyCollection<RelayUrl> relays,
        ITimelineQueryCompiler? compiler,
        TimeSpan? initialLoadTimeout,
        bool ownsBackend,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(relays);
        if (relays.Count == 0)
        {
            throw new ArgumentException("At least one relay is required.", nameof(relays));
        }

        _backend = backend;
        _relays = relays;
        _compiler = compiler ?? new SimpleTimelineQueryCompiler();
        _initialLoadTimeout = initialLoadTimeout ?? backend.Options.FetchTimeout;
        _ownsBackend = ownsBackend;
        _logger = logger ?? NullLogger.Instance;
    }

    public NostrBackendDiagnostics Diagnostics => _backend.Diagnostics;

    /// <summary>Read-only for now: the account is not consulted. See the class summary.</summary>
    public ITimelineSource Create(Timeline timeline, Account account) => CreateAnonymous(timeline);

    /// <exception cref="FilterParseException">The timeline's query is not understood (thrown here, synchronously, so the UI can report it before any relay traffic).</exception>
    public ITimelineSource CreateAnonymous(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var compiled = _compiler.Compile(timeline.KqlQuery);
        return new NostrTimelineSource(_backend, _backend.Mapper, compiled, EnsureConnectedAsync, _initialLoadTimeout, _backend.Options.MaxRememberedEventIdsPerTimeline, _logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsBackend)
        {
            await _backend.DisposeAsync().ConfigureAwait(false);
        }

        _connectGate.Dispose();
    }

    /// <summary>Connects the pool once. A failed attempt (no relay reachable) is not remembered, so the next stream retries.</summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connected)
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                return;
            }

            await _backend.ConnectAsync(_relays, cancellationToken).ConfigureAwait(false);
            _connected = true;
        }
        finally
        {
            _connectGate.Release();
        }
    }
}
