namespace ZkLink;

/// <summary>A definitively rejected atomic write multi with ordered member diagnostics.</summary>
public sealed class ZooKeeperMultiException : ZooKeeperException
{
    /// <summary>Snapshots all diagnostics; no member is considered committed.</summary>
    public ZooKeeperMultiException(
        ZooKeeperRequestInfo request,
        IReadOnlyList<ZooKeeperMultiDiagnostic> diagnostics,
        int failedMemberIndex
    )
        : this(request, Validate(diagnostics, failedMemberIndex), failedMemberIndex) { }

    private ZooKeeperMultiException(
        ZooKeeperRequestInfo request,
        System.Collections.ObjectModel.ReadOnlyCollection<ZooKeeperMultiDiagnostic> diagnostics,
        int failedMemberIndex
    )
        : base(request, RequestOutcome.Rejected, diagnostics[failedMemberIndex].ErrorCode)
    {
        if (request.Operation != ZooKeeperOperation.Multi)
            throw new ArgumentException(
                "Write multi failure requires a Multi request.",
                nameof(request)
            );
        Diagnostics = diagnostics;
        FailedMemberIndex = failedMemberIndex;
    }

    /// <summary>Gets the complete member list, including success-looking diagnostics.</summary>
    public IReadOnlyList<ZooKeeperMultiDiagnostic> Diagnostics { get; }

    /// <summary>Gets the zero-based index of the definitive failing member.</summary>
    public int FailedMemberIndex { get; }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ZooKeeperMultiDiagnostic> Validate(
        IReadOnlyList<ZooKeeperMultiDiagnostic> diagnostics,
        int failedMemberIndex
    )
    {
        var copy = Snapshot.List(diagnostics);
        ArgumentOutOfRangeException.ThrowIfNegative(failedMemberIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(failedMemberIndex, copy.Count);
        for (int i = 0; i < copy.Count; i++)
            if (copy[i].Index != i)
                throw new ArgumentException(
                    "Diagnostics must match input order without missing indices.",
                    nameof(diagnostics)
                );
        if (copy[failedMemberIndex].ErrorCode == 0)
            throw new ArgumentException(
                "The failing member must have a nonzero server error.",
                nameof(diagnostics)
            );
        return copy;
    }
}
