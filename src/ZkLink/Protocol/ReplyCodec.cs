using ZkLink.Configuration;

namespace ZkLink.Protocol;

internal static class ReplyCodec
{
    internal static WireReply Decode(
        ReadOnlySpan<byte> payload,
        int expectedXid,
        WireRequest request,
        ProtocolLimits limits
    )
    {
        _ = RequestCodec.Measure(expectedXid, request, limits);
        var reader = new JuteReader(payload, limits);
        var header = reader.ReadReplyHeader();
        if (header.Xid != expectedXid)
        {
            throw new InvalidDataException("Reply xid does not match the expected request.");
        }
        var result =
            header.Error != 0 ? new ErrorResult(header.Error)
            : request is MultiRequest multi ? ReadMulti(ref reader, multi, limits)
            : ReadBody(ref reader, request.Opcode, limits);
        reader.EnsureEnd();
        return new WireReply(header, result);
    }

    internal static WireNotification DecodeNotification(
        ReadOnlySpan<byte> payload,
        ProtocolLimits limits
    )
    {
        var reader = new JuteReader(payload, limits);
        var header = reader.ReadReplyHeader();
        if (header.Xid != -1 || header.Error != 0)
        {
            throw new InvalidDataException("Invalid notification header.");
        }
        var type = reader.ReadInt32();
        var state = reader.ReadInt32();
        var path = reader.ReadString();
        if (path is not null)
        {
            ValidateServerPath(path);
        }
        if (type != -1 && path is null)
        {
            throw new InvalidDataException("Node notification requires a path.");
        }
        reader.EnsureEnd();
        return new WireNotification(type, state, path, header.Zxid < 0 ? null : header.Zxid);
    }

    private static WireResult ReadBody(ref JuteReader reader, int opcode, ProtocolLimits limits) =>
        opcode switch
        {
            1 or 9 => new PathResult(ReadPath(ref reader)),
            15 or 19 or 21 => new PathResult(ReadPath(ref reader), reader.ReadStat()),
            3 or 5 or 7 => new StatResult(reader.ReadStat()),
            4 => new DataResult(reader.ReadBuffer(limits.MaxDataBytes), reader.ReadStat()),
            6 => new AclResult(ReadAcl(ref reader), reader.ReadStat()),
            8 => new StringsResult(ReadChildren(ref reader)),
            103 => new StringsResult(ReadPaths(ref reader)),
            12 => new StringsResult(ReadChildren(ref reader), reader.ReadStat()),
            104 => ReadCount(ref reader),
            107 => ReadIdentities(ref reader),
            2 or 11 or 13 or 17 or 18 or 100 or 101 or 105 or 106 or -11 => new EmptyResult(),
            _ => throw new InvalidDataException("Unsupported response shape or opcode."),
        };

    private static CountResult ReadCount(ref JuteReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("Negative descendant count.");
        }
        return new CountResult(count);
    }

    private static MultiResult ReadMulti(
        ref JuteReader reader,
        MultiRequest request,
        ProtocolLimits limits
    )
    {
        var count = request.Members.Count;
        if (
            count > limits.MaxCollectionElements
            || reader.Remaining < 9
            || count > (reader.Remaining - 9) / 9
        )
        {
            throw new InvalidDataException("Invalid or excessive expected multi count.");
        }
        var results = new WireMultiMember[count];
        for (var i = 0; i < count; i++)
        {
            var type = reader.ReadInt32();
            var done = reader.ReadBoolean();
            var error = reader.ReadInt32();
            if (done)
            {
                throw new InvalidDataException("Premature multi terminator.");
            }
            if (type == -1)
            {
                var bodyError = reader.ReadInt32();
                if (bodyError != error)
                {
                    throw new InvalidDataException("Inconsistent multi error fields.");
                }
                results[i] = new WireMultiMember(type, bodyError, new ErrorResult(bodyError));
                continue;
            }
            var expected = request.Members[i].Opcode is 19 or 21 ? 15 : request.Members[i].Opcode;
            if (type != expected || error != 0 || type is not (1 or 2 or 4 or 5 or 8 or 13 or 15))
            {
                throw new InvalidDataException("Unexpected multi result shape.");
            }
            results[i] = new WireMultiMember(type, 0, ReadBody(ref reader, type, limits));
        }
        if (reader.ReadInt32() != -1 || !reader.ReadBoolean() || reader.ReadInt32() != -1)
        {
            throw new InvalidDataException("Invalid multi terminator or extra member.");
        }
        return new MultiResult(results);
    }

    private static WireAcl[] ReadAcl(ref JuteReader reader)
    {
        var count = reader.ReadVectorCount(12);
        var result = new WireAcl[count];
        for (var i = 0; i < count; i++)
        {
            var permissions = reader.ReadInt32();
            var scheme = Required(reader.ReadString());
            var id = Required(reader.ReadString());
            if (permissions is < 0 or > 31 || scheme.Length == 0)
            {
                throw new InvalidDataException("Invalid ACL record.");
            }
            result[i] = new WireAcl(permissions, scheme, id);
        }
        return result;
    }

    private static IdentityResult ReadIdentities(ref JuteReader reader)
    {
        var count = reader.ReadVectorCount(8);
        var values = new ZooKeeperClientIdentity[count];
        for (var i = 0; i < count; i++)
        {
            var scheme = Required(reader.ReadString());
            var user = Required(reader.ReadString());
            if (scheme.Length == 0)
            {
                throw new InvalidDataException("Empty identity scheme.");
            }
            values[i] = new ZooKeeperClientIdentity(scheme, user);
        }
        return new IdentityResult(values);
    }

    private static string[] ReadStrings(ref JuteReader reader)
    {
        var count = reader.ReadVectorCount(4);
        var values = new string[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = Required(reader.ReadString());
        }
        return values;
    }

    private static string ReadPath(ref JuteReader reader)
    {
        var path = Required(reader.ReadString());
        ValidateServerPath(path);
        return path;
    }

    private static string[] ReadChildren(ref JuteReader reader)
    {
        var values = ReadStrings(ref reader);
        foreach (var value in values)
        {
            try
            {
                ZNodePath.ValidateName(value);
            }
            catch (ArgumentException)
            {
                throw new InvalidDataException("Invalid child-name record.");
            }
        }
        return values;
    }

    private static string[] ReadPaths(ref JuteReader reader)
    {
        var values = ReadStrings(ref reader);
        foreach (var value in values)
        {
            ValidateServerPath(value);
        }
        return values;
    }

    private static void ValidateServerPath(string path)
    {
        try
        {
            ZNodePath.Validate(path);
        }
        catch (ArgumentException)
        {
            throw new InvalidDataException("Invalid server path.");
        }
    }

    private static string Required(string? value) =>
        value ?? throw new InvalidDataException("Unexpected null protocol string.");
}
