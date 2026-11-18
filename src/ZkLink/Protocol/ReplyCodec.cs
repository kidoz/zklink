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
        return DecodeForValidatedRequest(payload, expectedXid, request, limits);
    }

    // Encoding already checked the request body. The transmitted xid and incoming reply
    // still need to be checked, including when the server returns an error.
    internal static WireReply DecodeForValidatedRequest(
        ReadOnlySpan<byte> payload,
        int expectedXid,
        WireRequest request,
        ProtocolLimits limits
    )
    {
        RequestCodec.ValidateXid(expectedXid, request.Opcode);
        var reader = new JuteReader(payload, limits);
        var header = reader.ReadReplyHeader();
        if (header.Xid != expectedXid)
        {
            throw new InvalidDataException("Reply xid does not match the expected request.");
        }
        return new WireReply(header, ReadReplyBody(ref reader, header, request, limits, out _));
    }

    internal static WireResult ReadReplyBody(
        ref JuteReader reader,
        ReplyHeader header,
        WireRequest request,
        ProtocolLimits limits,
        out int memberSessionError
    )
    {
        memberSessionError = 0;
        var result =
            header.Error != 0 ? new ErrorResult(header.Error)
            : request is MultiRequest multi
                ? ReadMulti(ref reader, multi, limits, out memberSessionError)
            : ReadBody(ref reader, request.Opcode, limits);
        reader.EnsureEnd();
        return result;
    }

    internal static WireNotification DecodeNotification(
        ReadOnlySpan<byte> payload,
        ProtocolLimits limits
    )
    {
        var reader = new JuteReader(payload, limits);
        var header = reader.ReadReplyHeader();
        if (header.Xid != Xids.Notification || header.Error != 0)
        {
            throw new InvalidDataException("Invalid notification header.");
        }
        var type = reader.ReadInt32();
        var state = reader.ReadInt32();
        var path = reader.ReadString();
        // The watch dispatcher validates node paths in the client's namespace.
        if (type == ZkLink.Watches.WatchProtocol.SessionEvent && path is not null)
        {
            ValidateServerPath(path);
        }
        reader.EnsureEnd();
        return new WireNotification(type, state, path, header.Zxid < 0 ? null : header.Zxid);
    }

    private static WireResult ReadBody(ref JuteReader reader, int opcode, ProtocolLimits limits) =>
        opcode switch
        {
            (int)ZooKeeperOperation.Create or (int)ZooKeeperOperation.Sync => new PathResult(
                ReadPath(ref reader)
            ),
            (int)ZooKeeperOperation.CreateWithStat
            or (int)ZooKeeperOperation.CreateContainer
            or (int)ZooKeeperOperation.CreateTtl => new PathResult(
                ReadPath(ref reader),
                reader.ReadStat()
            ),
            (int)ZooKeeperOperation.Exists
            or (int)ZooKeeperOperation.SetData
            or (int)ZooKeeperOperation.SetAcl => new StatResult(reader.ReadStat()),
            (int)ZooKeeperOperation.GetData => new DataResult(
                reader.ReadBuffer(limits.MaxDataBytes),
                reader.ReadStat()
            ),
            (int)ZooKeeperOperation.GetAcl => new AclResult(ReadAcl(ref reader), reader.ReadStat()),
            (int)ZooKeeperOperation.GetChildrenNames => new StringsResult(ReadChildren(ref reader)),
            (int)ZooKeeperOperation.GetEphemerals => new StringsResult(ReadPaths(ref reader)),
            (int)ZooKeeperOperation.GetChildren => new StringsResult(
                ReadChildren(ref reader),
                reader.ReadStat()
            ),
            (int)ZooKeeperOperation.GetAllChildrenNumber => ReadCount(ref reader),
            (int)ZooKeeperOperation.WhoAmI => ReadIdentities(ref reader),
            (int)ZooKeeperOperation.AddWatch => ReadWatchAcknowledgment(ref reader),
            (int)ZooKeeperOperation.Delete
            or ControlOpcode.Ping
            or (int)ZooKeeperOperation.Check
            or (int)ZooKeeperOperation.CheckWatches
            or (int)ZooKeeperOperation.RemoveWatches
            or ControlOpcode.Auth
            or ControlOpcode.SetWatches
            or ControlOpcode.SetWatches2
            or (int)ZooKeeperOperation.CloseSession => new EmptyResult(),
            _ => throw new InvalidDataException("Unsupported response shape or opcode."),
        };

    private static EmptyResult ReadWatchAcknowledgment(ref JuteReader reader)
    {
        // FinalRequestProcessor returns ErrorResponse(OK) even though Java registers
        // addWatch with no response record. It is a real success body on both pins.
        if (reader.ReadInt32() != ZkLink.Watches.WatchProtocol.Success)
            throw new InvalidDataException("Invalid add-watch acknowledgment status.");
        return new EmptyResult();
    }

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
        ProtocolLimits limits,
        out int memberSessionError
    )
    {
        var count = request.Members.Count;
        memberSessionError = 0;
        const int multiHeaderBytes = sizeof(int) + sizeof(byte) + sizeof(int);
        int availableHeaders = reader.Remaining / multiHeaderBytes;
        if (count > limits.MaxCollectionElements)
        {
            throw new InvalidDataException("Invalid or excessive expected multi count.");
        }
        // Bound allocation by the number of member headers that fit. Decode the available
        // prefix so a complete session error survives a missing member or terminator.
        var results = new WireMultiMember[Math.Min(count, availableHeaders)];
        for (var i = 0; i < results.Length; i++)
        {
            var type = reader.ReadInt32();
            var done = reader.ReadBoolean();
            var error = reader.ReadInt32();
            if (done)
            {
                throw new InvalidDataException("Premature multi terminator.");
            }
            if (type == MultiHeader.ErrorOrEnd)
            {
                var bodyError = reader.ReadInt32();
                if (bodyError != error)
                {
                    throw new InvalidDataException("Inconsistent multi error fields.");
                }
                memberSessionError = ErrorClassifier.SelectMemberSessionError(
                    memberSessionError,
                    bodyError
                );
                results[i] = new WireMultiMember(type, bodyError, new ErrorResult(bodyError));
                continue;
            }
            var expected = request.Members[i].Opcode
                is (int)ZooKeeperOperation.CreateContainer
                    or (int)ZooKeeperOperation.CreateTtl
                ? (int)ZooKeeperOperation.CreateWithStat
                : request.Members[i].Opcode;
            if (
                type != expected
                || error != 0
                || type
                    is not (
                        (int)ZooKeeperOperation.Create
                        or (int)ZooKeeperOperation.Delete
                        or (int)ZooKeeperOperation.GetData
                        or (int)ZooKeeperOperation.SetData
                        or (int)ZooKeeperOperation.GetChildrenNames
                        or (int)ZooKeeperOperation.Check
                        or (int)ZooKeeperOperation.CreateWithStat
                    )
            )
            {
                throw new InvalidDataException("Unexpected multi result shape.");
            }
            results[i] = new WireMultiMember(type, 0, ReadBody(ref reader, type, limits));
        }
        if (
            results.Length != count
            || reader.ReadInt32() != MultiHeader.ErrorOrEnd
            || !reader.ReadBoolean()
            || reader.ReadInt32() != MultiHeader.NoError
        )
        {
            throw new InvalidDataException("Invalid multi terminator or extra member.");
        }
        if (
            request.ReadOnly
            && results.Any(static member => member.Result is ErrorResult { Error: 0 })
        )
        {
            throw new InvalidDataException("A read error cannot represent success.");
        }
        if (
            !request.ReadOnly
            && results.Any(static member => member.Result is ErrorResult)
            && !results.Any(static member => member.Error != 0)
        )
        {
            throw new InvalidDataException(
                "Write placeholders require an explicit failing member."
            );
        }
        return new MultiResult(results, memberSessionError);
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
