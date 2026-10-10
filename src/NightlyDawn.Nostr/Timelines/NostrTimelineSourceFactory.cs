using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Profiles;
using NightlyDawn.Nostr.Publishing;
using NightlyDawn.Nostr.Relay;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// The relay-backed <see cref="ITimelineSourceFactory"/> (leaf 1e-b). Owns (or borrows) one <see cref="NostrBackend"/>,
/// connects it lazily on the first stream, and compiles each column's query through <see cref="ITimelineQueryCompiler"/>.
/// The timeline side is read-only — nothing on the <see cref="Create"/> path signs anything, and no key store is
/// needed to stream public notes. Signing enters only through <see cref="CreatePublisher"/>, which the host calls
/// separately and hands its own key store (L3c). <see cref="Create"/> currently ignores the account — account-bound
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
        ProfileStore = new ProfileStore(backend, backend.Mapper);
    }

    public NostrBackendDiagnostics Diagnostics => _backend.Diagnostics;

    /// <summary>Shares this factory's backend connection rather than opening a second one (plan §8): the host
    /// hands this to <c>AppServices.ProfileStore</c> alongside the timeline source factory itself.</summary>
    public IProfileStore ProfileStore { get; }

    /// <summary>
    /// Builds the publisher over <em>this</em> factory's backend, so posting and reading share one relay pool
    /// and one connection (plan §10, L3c) — the same arrangement as <see cref="ProfileStore"/>. The host hands
    /// the result to <c>AppServices.NotePublisher</c>; the App layer only ever sees Core's
    /// <see cref="INotePublisher"/> (B9).
    ///
    /// <para>The returned publisher connects the pool on its first publish
    /// (<see cref="ConnectOnDemandBackend"/>): this factory connects lazily on the first timeline stream, and
    /// publishing is not a stream, so without that step a post sent before the first column connected would
    /// fail as a connectivity error against a pool that was never dialled.</para>
    ///
    /// <para>Exists here rather than on <see cref="ITimelineSourceFactory"/> because only the host calls it,
    /// and the host already references this assembly. Putting it on the Core interface would oblige every
    /// factory — including <c>SampleTimelineSourceFactory</c>, which has no keys and no relays — to answer a
    /// question it has no business answering.</para>
    /// </summary>
    /// <param name="keyStore">Signs every event this publisher sends. The timeline never consults it.</param>
    public INotePublisher CreatePublisher(IKeyStore keyStore)
    {
        ArgumentNullException.ThrowIfNull(keyStore);
        return new NotePublisher(
            new ConnectOnDemandBackend(_backend, EnsureConnectedAsync),
            keyStore,
            _backend.Mapper,
            TimeProvider.System);
    }

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
