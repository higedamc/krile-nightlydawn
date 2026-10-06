namespace NightlyDawn.Nostr;

/// <summary>Tunables for <see cref="NostrBackend"/>. Defaults are conservative; every limit exists so that a hostile or broken relay cannot make the client allocate or wait without bound.</summary>
public sealed class NostrBackendOptions
{
    /// <summary>Allow <c>ws://</c> relays. Off by default; only for local development relays (plan §5, S3).</summary>
    public bool AllowInsecureWebSockets { get; init; }

    /// <summary>Upper bound for a single relay message. Larger frames close the connection (policy violation) and are counted in diagnostics.</summary>
    public int MaxMessageBytes { get; init; } = 512 * 1024;

    /// <summary>Per-subscription buffer between the relay receive loops and the consumer. When full, the oldest buffered message is dropped and counted; consumers that fall behind lose history rather than stalling every relay connection.</summary>
    public int MaxBufferedMessagesPerSubscription { get; init; } = 4096;

    /// <summary>Upper bound on distinct events a single <see cref="NostrBackend.FetchAsync"/> keeps. A relay that floods a REQ cannot grow memory past this; excess events are dropped and counted.</summary>
    public int MaxEventsPerFetch { get; init; } = 10_000;

    /// <summary>Relay-authored free text (OK / CLOSED reasons) is truncated to this many characters before it is stored or surfaced. It is never logged.</summary>
    public int MaxRelayReasonChars { get; init; } = 200;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Safety net for <see cref="NostrBackend.FetchAsync"/>: the fetch waits for EOSE from every connected relay, but never longer than this. A relay that never sends EOSE must not hang the caller forever.</summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="NostrBackend.PublishAsync"/> waits for a relay's <c>OK</c> before recording that relay as rejected with reason <c>timeout</c>.</summary>
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>Counters the UI/tests can read. Every dropped or rejected input is counted here instead of being logged with its payload.</summary>
public sealed class NostrBackendDiagnostics
{
    private long _invalidId;
    private long _invalidSignature;
    private long _malformedMessages;
    private long _oversizeMessages;
    private long _droppedBufferedMessages;
    private long _mappingFailures;
    private long _dispatchFaults;

    public long InvalidId => Interlocked.Read(ref _invalidId);
    public long InvalidSignature => Interlocked.Read(ref _invalidSignature);
    public long MalformedMessages => Interlocked.Read(ref _malformedMessages);
    public long OversizeMessages => Interlocked.Read(ref _oversizeMessages);
    public long DroppedBufferedMessages => Interlocked.Read(ref _droppedBufferedMessages);
    public long MappingFailures => Interlocked.Read(ref _mappingFailures);

    /// <summary>Exceptions that escaped the per-message dispatch — i.e. <em>our</em> bugs, not relay garbage. Expected to stay at zero; a non-zero value means a defect, so it is kept apart from <see cref="MalformedMessages"/>, which is normally non-zero on a hostile relay.</summary>
    public long DispatchFaults => Interlocked.Read(ref _dispatchFaults);

    internal void CountInvalidId() => Interlocked.Increment(ref _invalidId);
    internal void CountInvalidSignature() => Interlocked.Increment(ref _invalidSignature);
    internal void CountMalformedMessage() => Interlocked.Increment(ref _malformedMessages);
    internal void CountOversizeMessage() => Interlocked.Increment(ref _oversizeMessages);
    internal void CountDroppedBufferedMessage() => Interlocked.Increment(ref _droppedBufferedMessages);
    internal void CountMappingFailure() => Interlocked.Increment(ref _mappingFailures);
    internal void CountDispatchFault() => Interlocked.Increment(ref _dispatchFaults);
}
