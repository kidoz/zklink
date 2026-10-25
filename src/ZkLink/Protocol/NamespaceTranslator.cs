using ZkLink.Configuration;

namespace ZkLink.Protocol;

internal static class NamespaceTranslator
{
    private static readonly ProtocolLimits DefaultLimits = new();

    internal static WireRequest ToServer(
        WireRequest request,
        ChrootPath paths,
        ProtocolLimits? limits = null
    )
    {
        limits ??= DefaultLimits;
        _ = Measure(request, paths, limits);
        return TranslateRequest(request, paths);
    }

    internal static int Measure(WireRequest request, ChrootPath paths, ProtocolLimits limits)
    {
        var xid = request.Opcode switch
        {
            11 => -2,
            100 => -4,
            101 or 105 => -8,
            _ => 1,
        };
        var remaining = limits.MaxFrameBytes - RequestCodec.Measure(xid, request, limits);
        CheckExpansion(request, paths, ref remaining);
        return limits.MaxFrameBytes - remaining;
    }

    private static WireRequest TranslateRequest(WireRequest request, ChrootPath paths) =>
        request switch
        {
            PathRequest p => p with { Path = paths.ToServer(p.Path) },
            ReadRequest p => p with { Path = paths.ToServer(p.Path) },
            VersionRequest p => p with { Path = paths.ToServer(p.Path) },
            SetDataRequest p => p with { Path = paths.ToServer(p.Path) },
            CreateRequest p => p with { Path = paths.ToServer(p.Path, p.Flags is 2 or 3 or 6) },
            SetAclRequest p => p with { Path = paths.ToServer(p.Path) },
            WatchRequest p => p with { Path = paths.ToServer(p.Path) },
            RestoreRequest r => new RestoreRequest(
                r.Zxid,
                Translate(r.Data, paths),
                Translate(r.Exists, paths),
                Translate(r.Children, paths),
                r.Persistent is null ? null : Translate(r.Persistent, paths),
                r.Recursive is null ? null : Translate(r.Recursive, paths)
            ),
            MultiRequest m => new MultiRequest(
                m.ReadOnly,
                Array.AsReadOnly(
                    m.Members.Select(member => TranslateRequest(member, paths)).ToArray()
                )
            ),
            EmptyRequest or AuthRequest => request,
            _ => throw new InvalidDataException("Unsupported request namespace shape."),
        };

    internal static WireResult ToClient(WireResult result, int opcode, ChrootPath paths) =>
        result switch
        {
            PathResult p => p with { Path = paths.ToClient(p.Path) },
            StringsResult list when opcode == 103 => list with
            {
                Values = list.Values.Where(paths.Contains).Select(paths.ToClient).ToArray(),
            },
            MultiResult multi => new MultiResult(
                multi
                    .Members.Select(member =>
                        member with
                        {
                            Result = ToClient(member.Result, member.Opcode, paths),
                        }
                    )
                    .ToArray()
            ),
            _ => result,
        };

    internal static WireNotification ToClient(WireNotification notification, ChrootPath paths) =>
        notification.Path is null
            ? notification
            : notification with
            {
                Path = paths.ToClient(notification.Path),
            };

    private static System.Collections.ObjectModel.ReadOnlyCollection<string?> Translate(
        IReadOnlyList<string?> source,
        ChrootPath paths
    ) => Array.AsReadOnly(source.Select(path => (string?)paths.ToServer(path!)).ToArray());

    private static void CheckExpansion(WireRequest request, ChrootPath paths, ref int remaining)
    {
        (var path, var sequential) = request switch
        {
            PathRequest p => (p.Path, false),
            ReadRequest p => (p.Path, false),
            VersionRequest p => (p.Path, false),
            SetDataRequest p => (p.Path, false),
            CreateRequest p => (p.Path, p.Flags is 2 or 3 or 6),
            SetAclRequest p => (p.Path, false),
            WatchRequest p => (p.Path, false),
            _ => (null, false),
        };
        if (path is not null)
        {
            var added = paths.AddedBytes(path, sequential);
            if (added > remaining)
            {
                throw new InvalidDataException("Chroot expansion exceeds frame bounds.");
            }
            remaining -= added;
        }
        if (request is MultiRequest multi)
        {
            foreach (var member in multi.Members)
            {
                CheckExpansion(member, paths, ref remaining);
            }
        }
        if (request is RestoreRequest restore)
        {
            CheckPaths(restore.Data, paths, ref remaining);
            CheckPaths(restore.Exists, paths, ref remaining);
            CheckPaths(restore.Children, paths, ref remaining);
            if (restore.Persistent is not null)
            {
                CheckPaths(restore.Persistent, paths, ref remaining);
            }
            if (restore.Recursive is not null)
            {
                CheckPaths(restore.Recursive, paths, ref remaining);
            }
        }
    }

    private static void CheckPaths(
        IReadOnlyList<string?> values,
        ChrootPath paths,
        ref int remaining
    )
    {
        foreach (var value in values)
        {
            var added = paths.AddedBytes(value!);
            if (added > remaining)
            {
                throw new InvalidDataException("Chroot expansion exceeds frame bounds.");
            }
            remaining -= added;
        }
    }
}
