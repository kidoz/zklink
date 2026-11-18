using System.Collections.ObjectModel;
using System.Text;
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
        var size = Measure(request, paths, limits);
        return TranslateMeasured(request, paths, limits, size);
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
        return RequestCodec.Measure(xid, request, limits, paths);
    }

    internal static WireRequest TranslateMeasured(
        WireRequest request,
        ChrootPath paths,
        ProtocolLimits limits,
        int measuredSize
    )
    {
        var translation = new Translation(paths, limits, measuredSize);
        return request switch
        {
            MultiRequest m => translation.Multi(m),
            RestoreRequest r => new RestoreRequest(
                r.Zxid,
                translation.Paths(r.Data),
                translation.Paths(r.Exists),
                translation.Paths(r.Children),
                r.Persistent is null ? null : translation.Paths(r.Persistent),
                r.Recursive is null ? null : translation.Paths(r.Recursive)
            ),
            _ => translation.Single(request),
        };
    }

    // Caller-backed collections may change after admission. Bound the copies we make
    // from the current values before allocating them. The writer checks the complete record.
    private struct Translation(ChrootPath paths, ProtocolLimits limits, int measuredSize)
    {
        private int _remaining = measuredSize;

        internal WireRequest Single(WireRequest request) =>
            request switch
            {
                PathRequest p => p with { Path = Path(p.Path) },
                ReadRequest p => p with { Path = Path(p.Path) },
                VersionRequest p => p with { Path = Path(p.Path) },
                SetDataRequest p => p with { Path = Path(p.Path) },
                CreateRequest p => p with
                {
                    Path = Path(p.Path, p.Flags is 2 or 3 or 6),
                    Acl = Acl(p.Acl),
                },
                SetAclRequest p => p with { Path = Path(p.Path), Acl = Acl(p.Acl) },
                WatchRequest p => p with { Path = Path(p.Path) },
                PingRequest or EmptyRequest or AuthRequest => request,
                _ => throw new InvalidDataException("Unsupported request namespace shape."),
            };

        internal MultiRequest Multi(MultiRequest request)
        {
            var count = request.Members.Count;
            ReserveElements(count, 2 * sizeof(int) + sizeof(byte));
            var members = new WireRequest[count];
            for (int i = 0; i < count; i++)
            {
                members[i] = Single(request.Members[i]);
            }
            return new MultiRequest(request.ReadOnly, Array.AsReadOnly(members));
        }

        internal ReadOnlyCollection<string?> Paths(IReadOnlyList<string?> source)
        {
            var count = source.Count;
            ReserveElements(count, sizeof(int));
            var copy = new string?[count];
            for (int i = 0; i < count; i++)
            {
                copy[i] = Path(source[i]!);
            }
            return Array.AsReadOnly(copy);
        }

        private string Path(string path, bool sequential = false)
        {
            var added = paths.AddedBytes(path, sequential);
            if (added > 0)
            {
                ArgumentNullException.ThrowIfNull(path);
                var available = _remaining - added;
                int bytes;
                if (
                    path.Length > available
                    || (bytes = Encoding.UTF8.GetByteCount(path)) > available
                )
                {
                    throw new InvalidDataException("Chroot expansion exceeds the measured frame.");
                }
                _remaining -= bytes + added;
            }
            return paths.ToServer(path, sequential);
        }

        private ReadOnlyCollection<WireAcl> Acl(IReadOnlyList<WireAcl> entries)
        {
            const int minimumEntryBytes = 3 * sizeof(int);
            var copy = Snapshot.List(
                entries,
                Math.Min(limits.MaxCollectionElements, _remaining / minimumEntryBytes)
            );
            _remaining -= copy.Count * minimumEntryBytes;
            return copy;
        }

        private void ReserveElements(int count, int minimumBytes)
        {
            if (
                count < 0
                || count > limits.MaxCollectionElements
                || count > _remaining / minimumBytes
            )
            {
                throw new InvalidDataException("Collection exceeds the measured frame.");
            }
            _remaining -= count * minimumBytes;
        }
    }

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
}
