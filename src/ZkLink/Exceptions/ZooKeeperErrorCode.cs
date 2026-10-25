namespace ZkLink;

/// <summary>Known server failure codes; unknown numeric codes remain inspectable separately.</summary>
public enum ZooKeeperErrorCode
{
    /// <summary>Server failure code -1.</summary>
    SystemError = -1,

    /// <summary>Server failure code -2.</summary>
    RuntimeInconsistency = -2,

    /// <summary>Server failure code -3.</summary>
    DataInconsistency = -3,

    /// <summary>Server failure code -4.</summary>
    ConnectionLoss = -4,

    /// <summary>Server failure code -5.</summary>
    MarshallingError = -5,

    /// <summary>Server failure code -6.</summary>
    Unimplemented = -6,

    /// <summary>Server failure code -7.</summary>
    OperationTimeout = -7,

    /// <summary>Server failure code -8.</summary>
    BadArguments = -8,

    /// <summary>Server failure code -12.</summary>
    UnknownSession = -12,

    /// <summary>Server failure code -13.</summary>
    NewConfigNoQuorum = -13,

    /// <summary>Server failure code -14.</summary>
    ReconfigurationInProgress = -14,

    /// <summary>Server failure code -101.</summary>
    NoNode = -101,

    /// <summary>Server failure code -102.</summary>
    NoAuth = -102,

    /// <summary>Server failure code -103.</summary>
    BadVersion = -103,

    /// <summary>Server failure code -108.</summary>
    NoChildrenForEphemerals = -108,

    /// <summary>Server failure code -110.</summary>
    NodeExists = -110,

    /// <summary>Server failure code -111.</summary>
    NotEmpty = -111,

    /// <summary>Server failure code -112.</summary>
    SessionExpired = -112,

    /// <summary>Server failure code -113.</summary>
    InvalidCallback = -113,

    /// <summary>Server failure code -114.</summary>
    InvalidAcl = -114,

    /// <summary>Server failure code -115.</summary>
    AuthFailed = -115,

    /// <summary>Server failure code -118.</summary>
    SessionMoved = -118,

    /// <summary>Server failure code -119.</summary>
    NotReadOnly = -119,

    /// <summary>Server failure code -120.</summary>
    EphemeralOnLocalSession = -120,

    /// <summary>Server failure code -121.</summary>
    NoWatcher = -121,

    /// <summary>Server failure code -122.</summary>
    RequestTimeout = -122,

    /// <summary>Server failure code -123.</summary>
    ReconfigurationDisabled = -123,

    /// <summary>Server failure code -124.</summary>
    SessionClosedRequiresSaslAuth = -124,

    /// <summary>Server failure code -125.</summary>
    QuotaExceeded = -125,

    /// <summary>Server failure code -127.</summary>
    ThrottledOperation = -127,
}
