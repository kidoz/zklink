using System.Collections;
using ZkLink.Protocol;

namespace ZkLink;

public sealed partial class ZooKeeperClient
{
    /// <summary>Executes a nonempty atomic write transaction, returning typed results in input order.</summary>
    /// <remarks>Captures inputs before returning. Rejected transactions commit no members; sent transactions are never replayed.</remarks>
    public Task<IReadOnlyList<ZooKeeperWriteResult>> MultiAsync(
        IReadOnlyList<ZooKeeperWriteOperation> operations,
        CancellationToken cancellationToken = default
    ) =>
        ReadWriteMultiAsync(
            _session.Submit(
                new MultiRequest(
                    false,
                    new OperationView<ZooKeeperWriteOperation>(
                        operations,
                        op => ValidateCreateFeatures(op.Request)
                    )
                ),
                new ZooKeeperRequestInfo(ZooKeeperOperation.Multi),
                cancellationToken
            )
        );

    /// <summary>Executes a nonempty read batch, returning a typed result or error for every input member.</summary>
    /// <remarks>Read results do not form an atomic snapshot. Child results have names only, without parent Stat.</remarks>
    public Task<IReadOnlyList<ZooKeeperReadResult>> MultiReadAsync(
        IReadOnlyList<ZooKeeperReadOperation> operations,
        CancellationToken cancellationToken = default
    ) =>
        ReadReadMultiAsync(
            _session.Submit(
                new MultiRequest(
                    true,
                    new OperationView<ZooKeeperReadOperation>(operations, static op => op.Request)
                ),
                new ZooKeeperRequestInfo(ZooKeeperOperation.MultiRead),
                cancellationToken
            )
        );

    private static async Task<IReadOnlyList<ZooKeeperWriteResult>> ReadWriteMultiAsync(
        Task<WireReply> task
    )
    {
        var multi = (MultiResult)(await task.ConfigureAwait(false)).Result;
        var results = new ZooKeeperWriteResult[multi.Members.Length];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = multi.Members[i] switch
            {
                { Result: PathResult { Stat: { } stat } path } => new ZooKeeperCreatedResult(
                    new CreateResult(path.Path, ToPublic(stat))
                ),
                { Result: StatResult stat } => new ZooKeeperDataSetResult(ToPublic(stat.Stat)),
                { Opcode: 2 } => new ZooKeeperDeletedResult(),
                { Opcode: 13 } => new ZooKeeperCheckedResult(),
                _ => throw new InvalidDataException("Unexpected successful write member."),
            };
        }
        return Array.AsReadOnly(results);
    }

    private static async Task<IReadOnlyList<ZooKeeperReadResult>> ReadReadMultiAsync(
        Task<WireReply> task
    )
    {
        var multi = (MultiResult)(await task.ConfigureAwait(false)).Result;
        var results = new ZooKeeperReadResult[multi.Members.Length];
        for (var i = 0; i < results.Length; i++)
        {
            results[i] = multi.Members[i].Result switch
            {
                DataResult data => new ZooKeeperDataReadResult(
                    new ZNodeData(
                        data.Data is null ? (ReadOnlyMemory<byte>?)null : data.Data,
                        ToPublic(data.Stat)
                    )
                ),
                StringsResult children => new ZooKeeperChildrenReadResult(children.Values),
                ErrorResult error => new ZooKeeperReadErrorResult(error.Error),
                _ => throw new InvalidDataException("Unexpected read member."),
            };
        }
        return Array.AsReadOnly(results);
    }

    // A view permits whole-batch measurement before the scheduler reserves bytes and copies inputs.
    private sealed class OperationView<T> : IReadOnlyList<WireRequest>
        where T : class
    {
        private readonly IReadOnlyList<T> _operations;
        private readonly Func<T, WireRequest> _request;

        internal OperationView(IReadOnlyList<T> operations, Func<T, WireRequest> request)
        {
            ArgumentNullException.ThrowIfNull(operations);
            if (operations.Count == 0)
            {
                throw new ArgumentException(
                    "A transaction requires at least one operation.",
                    nameof(operations)
                );
            }
            _operations = operations;
            _request = request;
        }

        public int Count => _operations.Count;
        public WireRequest this[int index] =>
            _request(
                _operations[index]
                    ?? throw new ArgumentException("Transaction members cannot be null.")
            );

        public IEnumerator<WireRequest> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
