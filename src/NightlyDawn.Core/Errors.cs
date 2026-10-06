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

/// <summary>No signer is connected (NIP-07 absent, NIP-46 never paired, no local key imported).</summary>
public sealed class SignerUnavailableException(string message) : NightlyDawnException(message);

/// <summary>A NIP-46 remote signer request exceeded its timeout (plan §5 D4: 30s).</summary>
public sealed class SignerRequestTimedOutException(string message) : NightlyDawnException(message);

public sealed class RelayConnectionException(string relayUrl, string message) : NightlyDawnException(message)
{
    public string RelayUrl { get; } = relayUrl;
}

/// <summary>A relay rejected a published event (NIP-20 <c>OK false &lt;reason&gt;</c>).</summary>
public sealed class EventPublishException(string relayUrl, string reason)
    : NightlyDawnException($"Relay {relayUrl} rejected the event: {reason}")
{
    public string RelayUrl { get; } = relayUrl;
    public string Reason { get; } = reason;
}

public sealed class FilterParseException(string message) : NightlyDawnException(message);
