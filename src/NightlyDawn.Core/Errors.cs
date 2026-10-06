namespace NightlyDawn.Core;

public abstract class NightlyDawnException : Exception
{
    protected NightlyDawnException(string message) : base(message)
    {
    }

    protected NightlyDawnException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>No signer is connected — NIP-46 never paired, or no local key generated/imported (B2: NIP-07 is not a signer path here).</summary>
public sealed class SignerUnavailableException(string message) : NightlyDawnException(message);

/// <summary>A NIP-46 remote signer request exceeded its timeout (plan §5 D4: 30s).</summary>
public sealed class SignerRequestTimedOutException(string message) : NightlyDawnException(message);

public sealed class RelayConnectionException(string relayUrl, string message) : NightlyDawnException(message)
{
    public string RelayUrl { get; } = relayUrl;
}

/// <summary>No relay accepted a published event (B5: zero-of-N accepted, not an individual relay's rejection — see <see cref="Result"/> for the per-relay detail).</summary>
public sealed class EventPublishException(PublishResult result)
    : NightlyDawnException("No relay accepted the event.")
{
    public PublishResult Result { get; } = result;
}

public sealed class FilterParseException(string message) : NightlyDawnException(message);

/// <summary>A wire-level event could not be mapped onto a domain type (malformed kind:0/10002/1 content). Callers should skip and log, not crash the subscription (B10).</summary>
public sealed class EventMappingException(string message) : NightlyDawnException(message);
