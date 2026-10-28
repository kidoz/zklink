namespace ZkLink.AotSmoke;

internal static class NativeSecurity
{
    internal static async Task VerifyAsync(
        NativeSettings settings,
        ZooKeeperClient client,
        CancellationToken token
    )
    {
        var path = "/zklink-native-acl-" + Guid.NewGuid().ToString("N");
        await client
            .CreateAsync(
                path,
                "protected"u8.ToArray(),
                ZkLink.Samples.Basic.SampleNodes.EphemeralCreatorAcl(),
                token
            )
            .ConfigureAwait(false);
        try
        {
            var acl = await client.GetAclAsync(path, token).ConfigureAwait(false);
            var digest = acl.Acl.Single(item => item.Scheme == "digest");
            await client.SetAclAsync(path, [digest], ZNodeVersion.Any, token).ConfigureAwait(false);
            var data = await client.GetDataAsync(path, token).ConfigureAwait(false);
            NativeSettings.Require(
                data.Data is { } bytes && bytes.Span.SequenceEqual("protected"u8)
            );
            var identities = await client.WhoAmIAsync(token).ConfigureAwait(false);
            NativeSettings.Require(
                identities.Any(item => item.Scheme == "digest")
                    && identities.Any(item => item.Scheme == "x509") == settings.Mutual
            );
            var other = await settings
                .ConnectAsync(settings.Endpoint, token, digest: false)
                .ConfigureAwait(false);
            await using var otherLifetime = other.ConfigureAwait(false);
            await DeniedAsync(other, path, token).ConfigureAwait(false);
            if (settings.Mutual)
            {
                await client
                    .SetAclAsync(
                        path,
                        [new ZooKeeperAcl(ZooKeeperPermissions.All, "auth", "")],
                        ZNodeVersion.Any,
                        token
                    )
                    .ConfigureAwait(false);
                var certificateAcl = await other.GetAclAsync(path, token).ConfigureAwait(false);
                var x509 = certificateAcl.Acl.Single(item => item.Scheme == "x509");
                await client
                    .SetAclAsync(path, [x509], ZNodeVersion.Any, token)
                    .ConfigureAwait(false);
                _ = await other.GetDataAsync(path, token).ConfigureAwait(false);
                var different = await settings
                    .ConnectAsync(
                        settings.Endpoint,
                        token,
                        digest: false,
                        tls: settings.Tls(settings.OtherCertificate)
                    )
                    .ConfigureAwait(false);
                await using var differentLifetime = different.ConfigureAwait(false);
                await DeniedAsync(different, path, token).ConfigureAwait(false);
                await NativeSettings.CloseAsync(different, token).ConfigureAwait(false);
            }

            await NativeSettings.CloseAsync(other, token).ConfigureAwait(false);
        }
        finally
        {
            // Delete authorization belongs to the world-readable parent, not the child's ACL.
            await client.DeleteAsync(path, ZNodeVersion.Any, token).ConfigureAwait(false);
        }

        await Console
            .Out.WriteLineAsync("Native TLS/digest and distinct ACL identities: verified.")
            .ConfigureAwait(false);
    }

    internal static async Task RejectionsAsync(NativeSettings settings, CancellationToken token)
    {
        var certificate = settings.Mutual ? settings.Certificate : null;
        await RejectAsync(settings.Endpoint, settings.Tls(certificate, name: "wrong.zklink.test"))
            .ConfigureAwait(false);
        await RejectAsync(
                settings.Endpoint,
                settings.Tls(certificate, root: settings.UntrustedRoot)
            )
            .ConfigureAwait(false);
        await RejectAsync(
                NativeSettings.Required("ZKLINK_AOT_EXPIRED_ENDPOINT"),
                settings.Tls(certificate)
            )
            .ConfigureAwait(false);
        await RejectAsync(
                NativeSettings.Required("ZKLINK_AOT_PLAINTEXT_ENDPOINT"),
                settings.Tls(certificate)
            )
            .ConfigureAwait(false);
        if (settings.Mutual)
        {
            await RejectAsync(settings.Endpoint, settings.Tls(null)).ConfigureAwait(false);
        }

        await Console
            .Out.WriteLineAsync(
                "Native certificate/name/trust/plaintext rejection controls: verified."
            )
            .ConfigureAwait(false);

        async Task RejectAsync(string endpoint, ZooKeeperTlsOptions tls)
        {
            try
            {
                var unexpected = await settings
                    .ConnectAsync(endpoint, token, tls: tls)
                    .ConfigureAwait(false);
                await unexpected.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Invalid TLS configuration was accepted.");
            }
            catch (ZooKeeperConnectionException error)
                when (error.Failure == ZooKeeperConnectionFailure.Tls)
            {
                // Require TLS failure rather than treating timeout or any connection error as success.
            }
        }
    }

    private static async Task DeniedAsync(
        ZooKeeperClient client,
        string path,
        CancellationToken token
    )
    {
        try
        {
            _ = await client.GetDataAsync(path, token).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Protected data was readable without its identity."
            );
        }
        catch (ZooKeeperException error)
            when (error.KnownErrorCode == ZooKeeperErrorCode.NoAuth
                && error.Outcome == RequestOutcome.Rejected
            )
        {
            await client.SyncAsync("/", token).ConfigureAwait(false);
            NativeSettings.Require(client.Session.State == ZooKeeperSessionState.Connected);
        }
    }
}
