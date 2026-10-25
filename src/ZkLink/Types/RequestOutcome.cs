namespace ZkLink;

/// <summary>Describes what is known about a failed request when its caller completes.</summary>
public enum RequestOutcome
{
    /// <summary>No request bytes were handed to the transport.</summary>
    NotSent = 0,

    /// <summary>A definitive server response establishes rejection.</summary>
    Rejected = 1,

    /// <summary>Bytes may have left the client without a definitive result.</summary>
    Unknown = 2,
}
