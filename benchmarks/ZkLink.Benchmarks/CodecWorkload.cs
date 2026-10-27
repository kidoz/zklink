using System.Globalization;
using ZkLink.Protocol;

namespace ZkLink.Benchmarks;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Maintainability",
    "CA1515",
    Justification = "BenchmarkDotNet generates a separate assembly that references this public benchmark type."
)]
public enum CodecCase
{
    GetSmall,
    GetLarge,
    SetSmall,
    SetLarge,
    ChildrenSmall,
    ChildrenLarge,
    MultiSmall,
    MultiLarge,
}

internal sealed class CodecWorkload
{
    internal const int SmallPayloadBytes = 32;
    internal const int LargePayloadBytes = 64 * 1024;
    private const int SmallChildren = 8;
    private const int LargeChildren = 1024;
    private const int SmallTransaction = 4;
    private const int LargeTransaction = 32;
    private const int Xid = 1;
    private const long Zxid = 1;
    private const int Success = 0;
    private const int MultiTerminator = -1;
    private const string NodePath = "/benchmark/node";
    private static readonly ProtocolLimits Limits = new();
    private readonly WireRequest _request;
    private readonly byte[] _reply;
    private readonly byte[] _destination;
    private readonly byte[] _data;
    private readonly string[] _children;
    private readonly int _members;

    internal CodecWorkload(CodecCase workload)
    {
        _data = new byte[
            workload is CodecCase.GetLarge or CodecCase.SetLarge
                ? LargePayloadBytes
                : SmallPayloadBytes
        ];
        _data.AsSpan().Fill(42);
        _children = Enumerable
            .Range(0, workload == CodecCase.ChildrenLarge ? LargeChildren : SmallChildren)
            .Select(static index => "child-" + index.ToString("D4", CultureInfo.InvariantCulture))
            .ToArray();
        _members = workload == CodecCase.MultiLarge ? LargeTransaction : SmallTransaction;
        _request = workload switch
        {
            CodecCase.GetSmall or CodecCase.GetLarge => new ReadRequest(
                ZooKeeperOperation.GetData,
                NodePath,
                false
            ),
            CodecCase.SetSmall or CodecCase.SetLarge => new SetDataRequest(
                NodePath,
                _data,
                ZNodeVersion.Any.Value
            ),
            CodecCase.ChildrenSmall or CodecCase.ChildrenLarge => new ReadRequest(
                ZooKeeperOperation.GetChildrenNames,
                NodePath,
                false
            ),
            CodecCase.MultiSmall or CodecCase.MultiLarge => new MultiRequest(
                false,
                Enumerable
                    .Range(0, _members)
                    .Select(_ =>
                        (WireRequest)new SetDataRequest(NodePath, _data, ZNodeVersion.Any.Value)
                    )
                    .ToArray()
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(workload)),
        };
        _destination = new byte[RequestCodec.Measure(Xid, _request, Limits)];
        var buffer = new byte[Limits.MaxFrameBytes];
        var writer = new JuteWriter(buffer, Limits);
        writer.WriteInt32(Xid);
        writer.WriteInt64(Zxid);
        writer.WriteInt32(Success);
        var stat = new WireStat(1, 1, 1, 1, 0, 0, 0, 0, _data.Length, 0, 1);
        switch (_request)
        {
            case ReadRequest { Type: ZooKeeperOperation.GetData }:
                writer.WriteBuffer(_data);
                writer.WriteStat(stat);
                break;
            case SetDataRequest:
                writer.WriteStat(stat);
                break;
            case ReadRequest { Type: ZooKeeperOperation.GetChildrenNames }:
                writer.WriteStringVector(_children);
                break;
            case MultiRequest:
                for (int index = 0; index < _members; index++)
                {
                    writer.WriteInt32((int)ZooKeeperOperation.SetData);
                    writer.WriteBoolean(false);
                    writer.WriteInt32(Success);
                    writer.WriteStat(stat);
                }
                writer.WriteInt32(MultiTerminator);
                writer.WriteBoolean(true);
                writer.WriteInt32(MultiTerminator);
                break;
        }
        _reply = buffer.AsSpan(0, writer.WrittenCount).ToArray();
        Validate();
    }

    internal int RequestBytes => _destination.Length;
    internal int ReplyBytes => _reply.Length;

    internal int Encode() => RequestCodec.Encode(_destination, Xid, _request, Limits);

    internal object Decode() => ReplyCodec.Decode(_reply, Xid, _request, Limits);

    private void Validate()
    {
        if (Encode() != _destination.Length)
        {
            throw new InvalidDataException("Benchmark request length mismatch.");
        }
        var reply = (WireReply)Decode();
        bool valid = reply.Result switch
        {
            DataResult result => result.Data is not null
                && result.Data.AsSpan().SequenceEqual(_data)
                && result.Stat.DataLength == _data.Length,
            StatResult result => result.Stat.DataLength == _data.Length,
            StringsResult result => result.Values.SequenceEqual(_children),
            MultiResult result => result.Members.Length == _members
                && result.Members.All(member =>
                    member.Opcode == (int)ZooKeeperOperation.SetData
                    && member.Error == Success
                    && member.Result is StatResult { Stat.DataLength: SmallPayloadBytes }
                ),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException("Benchmark reply content mismatch.");
        }
    }
}
