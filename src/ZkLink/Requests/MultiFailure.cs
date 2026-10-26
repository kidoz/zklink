using ZkLink.Protocol;

namespace ZkLink.Requests;

internal static class MultiFailure
{
    internal static ZooKeeperMultiException? Create(ZooKeeperRequestInfo request, MultiResult multi)
    {
        var failed = Array.FindIndex(multi.Members, static member => member.Error != 0);
        if (failed < 0)
        {
            return null;
        }
        var diagnostics = new ZooKeeperMultiDiagnostic[multi.Members.Length];
        for (var i = 0; i < diagnostics.Length; i++)
        {
            var member = multi.Members[i];
            string? path = (member.Result as PathResult)?.Path;
            WireStat? stat = member.Result switch
            {
                PathResult returned => returned.Stat,
                StatResult returned => returned.Stat,
                _ => null,
            };
            diagnostics[i] = new ZooKeeperMultiDiagnostic(
                i,
                member.Opcode,
                member.Error,
                path,
                stat is { } value ? ZooKeeperClient.ToPublic(value) : null
            );
        }
        return new ZooKeeperMultiException(request, diagnostics, failed);
    }
}
