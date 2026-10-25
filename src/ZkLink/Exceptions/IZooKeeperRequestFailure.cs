namespace ZkLink;

/// <summary>Common request evidence exposed by ordinary and cancellation exceptions.</summary>
public interface IZooKeeperRequestFailure
{
    /// <summary>Gets request metadata captured when the caller completes.</summary>
    ZooKeeperRequestInfo Request { get; }

    /// <summary>Gets what is known about transmission and rejection.</summary>
    RequestOutcome Outcome { get; }

    /// <summary>Gets the original server code, or null for a local failure.</summary>
    int? ErrorCode { get; }

    /// <summary>Gets a known error enum when the numeric code is recognized.</summary>
    ZooKeeperErrorCode? KnownErrorCode { get; }
}
