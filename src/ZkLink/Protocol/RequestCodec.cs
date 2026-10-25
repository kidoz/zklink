using ZkLink.Configuration;

namespace ZkLink.Protocol;

internal static class RequestCodec
{
    internal static int Measure(int xid, WireRequest request, ProtocolLimits limits)
    {
        var output = new JuteSizer(limits);
        Write(ref output, xid, request, limits);
        return output.WrittenCount;
    }

    internal static int Encode(
        Span<byte> destination,
        int xid,
        WireRequest request,
        ProtocolLimits limits
    )
    {
        // A complete validating pass prevents partial encoding of an invalid multi.
        var size = Measure(xid, request, limits);
        if (size > destination.Length)
        {
            throw new InvalidDataException("Request destination is too small.");
        }
        var output = new JuteWriter(destination, limits);
        Write(ref output, xid, request, limits);
        return output.WrittenCount;
    }

    internal static void ValidateXid(int xid, int opcode)
    {
        var valid = opcode switch
        {
            11 => xid == -2,
            100 => xid == -4,
            101 or 105 => xid == -8,
            _ => xid > 0,
        };
        if (!valid)
        {
            throw new InvalidDataException("Invalid request xid for opcode.");
        }
    }

    private static void Write<T>(ref T output, int xid, WireRequest request, ProtocolLimits limits)
        where T : IJuteOutput, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateXid(xid, request.Opcode);
        output.WriteInt32(xid);
        output.WriteInt32(request.Opcode);
        WriteBody(ref output, request, limits);
    }

    private static void WriteBody<T>(ref T output, WireRequest request, ProtocolLimits limits)
        where T : IJuteOutput, allows ref struct
    {
        switch (request)
        {
            case EmptyRequest { Opcode: 11 or 107 or -11 }:
                break;
            case PathRequest path when path.Opcode is 6 or 9 or 103 or 104:
                Path(ref output, path.Path);
                break;
            case ReadRequest read when read.Opcode is 3 or 4 or 8 or 12:
                Path(ref output, read.Path);
                output.WriteBoolean(read.Watch);
                break;
            case VersionRequest version when version.Opcode is 2 or 13:
                Path(ref output, version.Path);
                output.WriteInt32(version.Version);
                break;
            case SetDataRequest set:
                Path(ref output, set.Path);
                Data(ref output, set.Data, limits);
                output.WriteInt32(set.Version);
                break;
            case CreateRequest create when create.Opcode is 1 or 15 or 19 or 21:
                ValidateCreate(create);
                Path(ref output, create.Path, create.Flags is 2 or 3 or 6);
                Data(ref output, create.Data, limits);
                Acl(ref output, create.Acl, limits);
                output.WriteInt32(create.Flags);
                if (create.Opcode == 21)
                {
                    output.WriteInt64(create.Ttl!.Value);
                }
                break;
            case SetAclRequest acl:
                Path(ref output, acl.Path);
                Acl(ref output, acl.Acl, limits);
                output.WriteInt32(acl.Version);
                break;
            case WatchRequest watch when watch.Opcode is 17 or 18 or 106:
                if (watch.Opcode == 106 ? watch.Mode is < 0 or > 1 : watch.Mode is < 1 or > 5)
                {
                    throw new InvalidDataException("Invalid watch category or mode.");
                }
                Path(ref output, watch.Path);
                output.WriteInt32(watch.Mode);
                break;
            case AuthRequest auth:
                if (
                    string.IsNullOrEmpty(auth.Scheme)
                    || string.Equals(auth.Scheme, "sasl", StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new InvalidDataException("Invalid or unsupported authentication scheme.");
                }
                output.WriteInt32(0);
                output.WriteString(auth.Scheme);
                output.WriteBuffer(auth.Credentials.Span);
                break;
            case RestoreRequest restore:
                if ((restore.Persistent is null) != (restore.Recursive is null))
                {
                    throw new InvalidDataException("Both persistent restore vectors are required.");
                }
                output.WriteInt64(restore.Zxid);
                Paths(ref output, restore.Data, limits);
                Paths(ref output, restore.Exists, limits);
                Paths(ref output, restore.Children, limits);
                if (restore.Persistent is not null)
                {
                    Paths(ref output, restore.Persistent, limits);
                    Paths(ref output, restore.Recursive!, limits);
                }
                break;
            case MultiRequest multi:
                if (multi.Members.Count > limits.MaxCollectionElements)
                {
                    throw new InvalidDataException("Too many multi members.");
                }
                foreach (var member in multi.Members)
                {
                    var allowed = multi.ReadOnly
                        ? member is ReadRequest { Opcode: 4 or 8, Watch: false }
                        : member
                            is CreateRequest { Opcode: 1 or 15 or 19 or 21 }
                                or SetDataRequest
                                or VersionRequest { Opcode: 2 or 13 };
                    if (!allowed)
                    {
                        throw new InvalidDataException(
                            "Invalid, nested, watched or mixed multi member."
                        );
                    }
                    output.WriteInt32(member.Opcode);
                    output.WriteBoolean(false);
                    output.WriteInt32(-1);
                    WriteBody(ref output, member, limits);
                }
                output.WriteInt32(-1);
                output.WriteBoolean(true);
                output.WriteInt32(-1);
                break;
            default:
                throw new InvalidDataException("Unsupported request shape or opcode.");
        }
    }

    private static void Path<T>(ref T output, string path, bool sequential = false)
        where T : IJuteOutput, allows ref struct
    {
        ZNodePath.Validate(path, sequential);
        output.WriteString(path);
    }

    private static void Paths<T>(ref T output, IReadOnlyList<string?> paths, ProtocolLimits limits)
        where T : IJuteOutput, allows ref struct
    {
        if (paths.Count > limits.MaxCollectionElements)
        {
            throw new InvalidDataException("Too many restore paths.");
        }
        foreach (var path in paths)
        {
            ZNodePath.Validate(path!);
        }
        output.WriteStringVector(paths);
    }

    private static void Data<T>(ref T output, ReadOnlyMemory<byte>? data, ProtocolLimits limits)
        where T : IJuteOutput, allows ref struct
    {
        if (data is not { } bytes)
        {
            output.WriteNullBuffer();
            return;
        }
        if (bytes.Length > limits.MaxDataBytes)
        {
            throw new InvalidDataException("Data exceeds configured bounds.");
        }
        output.WriteBuffer(bytes.Span);
    }

    private static void Acl<T>(ref T output, IReadOnlyList<WireAcl> entries, ProtocolLimits limits)
        where T : IJuteOutput, allows ref struct
    {
        if (entries.Count is 0 || entries.Count > limits.MaxCollectionElements)
        {
            throw new InvalidDataException("Invalid ACL count.");
        }
        output.WriteInt32(entries.Count);
        foreach (var entry in entries)
        {
            if (
                entry.Permissions is < 0 or > 31
                || string.IsNullOrEmpty(entry.Scheme)
                || entry.Id is null
            )
            {
                throw new InvalidDataException("Invalid ACL entry.");
            }
            output.WriteInt32(entry.Permissions);
            output.WriteString(entry.Scheme);
            output.WriteString(entry.Id);
        }
    }

    private static void ValidateCreate(CreateRequest create)
    {
        if (
            create.Flags is < 0 or > 6
            || (create.Opcode == 19 && create.Flags != 4)
            || (create.Flags == 4 && create.Opcode != 19)
            || ((create.Flags is 5 or 6) != (create.Opcode == 21))
            || ((create.Opcode == 21) != create.Ttl.HasValue)
            || create.Ttl is <= 0 or > CreateOptions.MaxTtlMilliseconds
        )
        {
            throw new InvalidDataException("Invalid create mode, opcode or TTL.");
        }
    }
}
