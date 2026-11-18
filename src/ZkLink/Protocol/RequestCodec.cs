using ZkLink.Configuration;

namespace ZkLink.Protocol;

internal static class RequestCodec
{
    internal static int Measure(
        int xid,
        WireRequest request,
        ProtocolLimits limits,
        ChrootPath? paths = null
    )
    {
        var output = new JuteSizer(limits, paths);
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
        return WritePacket(destination, xid, request, limits);
    }

    // The scheduler owns this exact-size buffer and discards it on any preparation error.
    internal static WireRequest EncodeMeasured(
        Span<byte> destination,
        int xid,
        WireRequest request,
        ChrootPath paths,
        ProtocolLimits limits
    )
    {
        var translated = NamespaceTranslator.TranslateMeasured(
            request,
            paths,
            limits,
            destination.Length
        );
        if (WritePacket(destination, xid, translated, limits) != destination.Length)
        {
            throw new ArgumentException(
                "The request size changed during submission.",
                nameof(request)
            );
        }
        return translated;
    }

    private static int WritePacket(
        Span<byte> destination,
        int xid,
        WireRequest request,
        ProtocolLimits limits
    )
    {
        var output = new JuteWriter(destination, limits);
        Write(ref output, xid, request, limits);
        return output.WrittenCount;
    }

    internal static void ValidateXid(int xid, int opcode)
    {
        var expected = Xids.ForMeasurement(opcode);
        if (expected > 0 ? xid <= 0 : xid != expected)
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
            case PingRequest:
            case EmptyRequest
            {
                Type: ZooKeeperOperation.WhoAmI or ZooKeeperOperation.CloseSession,
            }:
                break;
            case PathRequest path
                when path.Type
                    is ZooKeeperOperation.GetAcl
                        or ZooKeeperOperation.Sync
                        or ZooKeeperOperation.GetEphemerals
                        or ZooKeeperOperation.GetAllChildrenNumber:
                Path(ref output, path.Path);
                break;
            case ReadRequest read
                when read.Type
                    is ZooKeeperOperation.Exists
                        or ZooKeeperOperation.GetData
                        or ZooKeeperOperation.GetChildrenNames
                        or ZooKeeperOperation.GetChildren:
                Path(ref output, read.Path);
                output.WriteBoolean(read.Watch);
                break;
            case VersionRequest version
                when version.Type is ZooKeeperOperation.Delete or ZooKeeperOperation.Check:
                Path(ref output, version.Path);
                output.WriteInt32(version.Version);
                break;
            case SetDataRequest set:
                Path(ref output, set.Path);
                Data(ref output, set.Data, limits);
                output.WriteInt32(set.Version);
                break;
            case CreateRequest create
                when create.Type
                    is ZooKeeperOperation.Create
                        or ZooKeeperOperation.CreateWithStat
                        or ZooKeeperOperation.CreateContainer
                        or ZooKeeperOperation.CreateTtl:
                ValidateCreate(create);
                Path(ref output, create.Path, CreateFlags.IsSequential(create.Flags));
                Data(ref output, create.Data, limits);
                Acl(ref output, create.Acl, limits);
                output.WriteInt32(create.Flags);
                if (create.Type == ZooKeeperOperation.CreateTtl)
                {
                    output.WriteInt64(create.Ttl!.Value);
                }
                break;
            case SetAclRequest acl:
                Path(ref output, acl.Path);
                Acl(ref output, acl.Acl, limits);
                output.WriteInt32(acl.Version);
                break;
            case WatchRequest watch
                when watch.Type
                    is ZooKeeperOperation.CheckWatches
                        or ZooKeeperOperation.RemoveWatches
                        or ZooKeeperOperation.AddWatch:
                if (
                    watch.Type == ZooKeeperOperation.AddWatch
                        ? watch.Mode is < 0 or > 1
                        : watch.Mode is < 1 or > 5
                )
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
                        ? member
                            is ReadRequest
                            {
                                Type: ZooKeeperOperation.GetData
                                    or ZooKeeperOperation.GetChildrenNames,
                                Watch: false,
                            }
                        : member
                            is CreateRequest
                                {
                                    Type: ZooKeeperOperation.Create
                                        or ZooKeeperOperation.CreateWithStat
                                        or ZooKeeperOperation.CreateContainer
                                        or ZooKeeperOperation.CreateTtl,
                                }
                                or SetDataRequest
                                or VersionRequest
                                {
                                    Type: ZooKeeperOperation.Delete or ZooKeeperOperation.Check,
                                };
                    if (!allowed)
                    {
                        throw new InvalidDataException(
                            "Invalid, nested, watched or mixed multi member."
                        );
                    }
                    output.WriteInt32(member.Opcode);
                    output.WriteBoolean(false);
                    output.WriteInt32(MultiHeader.NoError);
                    WriteBody(ref output, member, limits);
                }
                output.WriteInt32(MultiHeader.ErrorOrEnd);
                output.WriteBoolean(true);
                output.WriteInt32(MultiHeader.NoError);
                break;
            default:
                throw new InvalidDataException("Unsupported request shape or opcode.");
        }
    }

    private static void Path<T>(ref T output, string path, bool sequential = false)
        where T : IJuteOutput, allows ref struct
    {
        ZNodePath.Validate(path, sequential);
        output.WritePath(path, sequential);
    }

    private static void Paths<T>(ref T output, IReadOnlyList<string?> paths, ProtocolLimits limits)
        where T : IJuteOutput, allows ref struct
    {
        if (paths.Count > limits.MaxCollectionElements)
        {
            throw new InvalidDataException("Too many restore paths.");
        }
        output.WriteInt32(paths.Count);
        foreach (var path in paths)
        {
            Path(ref output, path!);
        }
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
            create.Flags
                is < (int)CreateMode.Persistent
                    or > (int)CreateMode.PersistentSequentialWithTtl
            || (
                create.Type == ZooKeeperOperation.CreateContainer
                && create.Flags != (int)CreateMode.Container
            )
            || (
                create.Flags == (int)CreateMode.Container
                && create.Type != ZooKeeperOperation.CreateContainer
            )
            || (
                (
                    create.Flags
                    is (int)CreateMode.PersistentWithTtl
                        or (int)CreateMode.PersistentSequentialWithTtl
                ) != (create.Type == ZooKeeperOperation.CreateTtl)
            )
            || ((create.Type == ZooKeeperOperation.CreateTtl) != create.Ttl.HasValue)
            || create.Ttl is <= 0 or > CreateOptions.MaxTtlMilliseconds
        )
        {
            throw new InvalidDataException("Invalid create mode, opcode or TTL.");
        }
    }
}
