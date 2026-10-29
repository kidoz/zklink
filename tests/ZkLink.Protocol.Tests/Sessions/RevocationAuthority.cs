using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ZkLink.Protocol.Tests.Sessions;

// A unique issuer and distribution point per case avoid OS revocation-cache reuse.
internal sealed class RevocationAuthority : IAsyncDisposable
{
    private const int KeyBits = 2048;
    private const int SerialBytes = 16;
    private const int MaximumHeaderBytes = 4096;
    private const int MaximumRequests = 16;
    private const int InitialCrlNumber = 1;
    private const byte InvalidSignatureBit = 0x01;
    private const int UniformResourceIdentifierTag = 6;
    private const int ExplicitValueTag = 0;
    private const int ResponderNameTag = 1;
    private const int RevokedStatusTag = 1;
    private const int GoodStatusTag = 0;
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string AuthorityInformationAccessOid = "1.3.6.1.5.5.7.1.1";
    private const string OcspAccessOid = "1.3.6.1.5.5.7.48.1";
    private const string BasicOcspResponseOid = "1.3.6.1.5.5.7.48.1.1";
    private const string Sha1IdentifierOid = "1.3.14.3.2.26";
    private const string Sha256RsaSignatureOid = "1.2.840.113549.1.1.11";
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop;
    private readonly Task _server;
    private readonly byte[] _crl;
    private readonly byte[] _ocsp;
    private readonly bool _unavailable;
    private readonly TaskCompletionSource _respond = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly TaskCompletionSource _requested = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private int _requests;

    internal RevocationAuthority(
        bool revoked = false,
        bool unavailable = false,
        bool holdResponse = false,
        bool invalidSignature = false
    )
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        _stop.CancelAfter(Lifetime);
        _unavailable = unavailable;
        try
        {
            _listener.Start();
            // Isolate Apple responder caches by host as well as issuer, path and port.
            string responderHost = OperatingSystem.IsMacOS()
                ? $"zklink-{Guid.NewGuid():N}.localhost"
                : "127.0.0.1";
            string uriPrefix =
                $"http://{responderHost}:{((IPEndPoint)_listener.LocalEndpoint).Port}/{Guid.NewGuid():N}";
            using var key = RSA.Create(KeyBits);
            var rootRequest = new CertificateRequest(
                "CN=ZkLink revocation " + Guid.NewGuid().ToString("N"),
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            rootRequest.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(true, false, 0, true)
            );
            rootRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                    true
                )
            );
            rootRequest.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false)
            );
            var now = DateTimeOffset.UtcNow;
            Root = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(2));
            using var serverKey = RSA.Create(KeyBits);
            var request = new CertificateRequest(
                "CN=" + ServerName,
                serverKey,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true)
            );
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true)
            );
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid(ServerAuthenticationOid) },
                    true
                )
            );
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(ServerName);
            request.CertificateExtensions.Add(names.Build());
            var access = new AsnWriter(AsnEncodingRules.DER);
            access.PushSequence();
            access.PushSequence();
            access.WriteObjectIdentifier(OcspAccessOid);
            access.WriteCharacterString(
                UniversalTagNumber.IA5String,
                uriPrefix + "/ocsp",
                new Asn1Tag(TagClass.ContextSpecific, UniformResourceIdentifierTag)
            );
            access.PopSequence();
            access.PopSequence();
            request.CertificateExtensions.Add(
                new X509Extension(AuthorityInformationAccessOid, access.Encode(), false)
            );
            request.CertificateExtensions.Add(
                CertificateRevocationListBuilder.BuildCrlDistributionPointExtension([
                    uriPrefix + "/status.crl",
                ])
            );
            using var certificate = request.Create(
                Root,
                now.AddHours(-1),
                now.AddDays(1),
                RandomNumberGenerator.GetBytes(SerialBytes)
            );
            Server = ImportForTls(certificate, serverKey);
            var crl = new CertificateRevocationListBuilder();
            if (revoked)
            {
                crl.AddEntry(Server, now.AddMinutes(-1));
            }
            _crl = crl.Build(
                Root,
                crlNumber: InitialCrlNumber,
                nextUpdate: now.AddHours(1),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1,
                thisUpdate: now.AddMinutes(-1)
            );
            if (invalidSignature)
            {
                _crl[^1] ^= InvalidSignatureBit;
            }
            _ocsp = OcspResponse(now, revoked, invalidSignature);
            if (!holdResponse)
            {
                _respond.TrySetResult();
            }
            _server = ServeAsync();
        }
        catch
        {
            Server?.Dispose();
            Root?.Dispose();
            _listener.Stop();
            _listener.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    internal X509Certificate2 Root { get; }
    internal X509Certificate2 Server { get; }
    internal string ServerName { get; } = $"zklink-{Guid.NewGuid():N}.test";
    internal int Requests => Volatile.Read(ref _requests);
    internal Task Requested => _requested.Task;

    internal void Release() => _respond.TrySetResult();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350",
        Justification = "RFC 6960 CertID uses SHA-1 issuer identifiers; the OCSP response itself is signed with SHA-256. Test-only private issuer."
    )]
    private byte[] OcspResponse(DateTimeOffset now, bool revoked, bool invalidSignature)
    {
        // Test-only response syntax: RFC 6960 section 4.2. No production verifier.
        var explicitName = new Asn1Tag(
            TagClass.ContextSpecific,
            ResponderNameTag,
            isConstructed: true
        );
        var explicitNext = new Asn1Tag(
            TagClass.ContextSpecific,
            ExplicitValueTag,
            isConstructed: true
        );
        var data = new AsnWriter(AsnEncodingRules.DER);
        data.PushSequence();
        data.PushSequence(explicitName);
        data.WriteEncodedValue(Root.SubjectName.RawData);
        data.PopSequence(explicitName);
        data.WriteGeneralizedTime(now, omitFractionalSeconds: true);
        data.PushSequence();
        data.PushSequence();
        data.PushSequence();
        data.PushSequence();
        data.WriteObjectIdentifier(Sha1IdentifierOid);
        data.WriteNull();
        data.PopSequence();
        data.WriteOctetString(SHA1.HashData(Root.SubjectName.RawData));
        data.WriteOctetString(SHA1.HashData(Root.GetPublicKey()));
        data.WriteIntegerUnsigned(Server.SerialNumberBytes.Span);
        data.PopSequence();
        if (revoked)
        {
            var revokedTag = new Asn1Tag(
                TagClass.ContextSpecific,
                RevokedStatusTag,
                isConstructed: true
            );
            data.PushSequence(revokedTag);
            data.WriteGeneralizedTime(now.AddMinutes(-1), omitFractionalSeconds: true);
            data.PopSequence(revokedTag);
        }
        else
        {
            data.WriteNull(new Asn1Tag(TagClass.ContextSpecific, GoodStatusTag));
        }
        data.WriteGeneralizedTime(now.AddMinutes(-1), omitFractionalSeconds: true);
        data.PushSequence(explicitNext);
        data.WriteGeneralizedTime(now.AddHours(1), omitFractionalSeconds: true);
        data.PopSequence(explicitNext);
        data.PopSequence();
        data.PopSequence();
        data.PopSequence();
        byte[] encoded = data.Encode();
        using var key = Root.GetRSAPrivateKey()!;
        byte[] signature = key.SignData(
            encoded,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        if (invalidSignature)
        {
            signature[^1] ^= InvalidSignatureBit;
        }
        var basic = new AsnWriter(AsnEncodingRules.DER);
        basic.PushSequence();
        basic.WriteEncodedValue(encoded);
        basic.PushSequence();
        basic.WriteObjectIdentifier(Sha256RsaSignatureOid);
        basic.WriteNull();
        basic.PopSequence();
        basic.WriteBitString(signature);
        basic.PushSequence(explicitNext);
        basic.PushSequence();
        basic.WriteEncodedValue(Root.RawData);
        basic.PopSequence();
        basic.PopSequence(explicitNext);
        basic.PopSequence();
        var result = new AsnWriter(AsnEncodingRules.DER);
        result.PushSequence();
        result.WriteEnumeratedValue(OcspStatus.Successful);
        result.PushSequence(explicitNext);
        result.PushSequence();
        result.WriteObjectIdentifier(BasicOcspResponseOid);
        result.WriteOctetString(basic.Encode());
        result.PopSequence();
        result.PopSequence(explicitNext);
        result.PopSequence();
        return result.Encode();
    }

    private enum OcspStatus
    {
        Successful = 0,
    }

    private static X509Certificate2 ImportForTls(X509Certificate2 certificate, RSA key)
    {
        var result = certificate.CopyWithPrivateKey(key);
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }
        using (result)
        {
            byte[] pkcs12 = result.Export(X509ContentType.Pkcs12);
            try
            {
                return X509CertificateLoader.LoadPkcs12(
                    pkcs12,
                    null,
                    X509KeyStorageFlags.DefaultKeySet
                );
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs12);
            }
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener
                    .AcceptTcpClientAsync(_stop.Token)
                    .ConfigureAwait(false);
                try
                {
                    var stream = client.GetStream();
                    var header = new byte[MaximumHeaderBytes];
                    int length = 0;
                    int bodyStart;
                    while ((bodyStart = header.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
                    {
                        if (length == header.Length)
                        {
                            throw new InvalidDataException(
                                "Revocation test header limit exceeded."
                            );
                        }
                        int count = await stream
                            .ReadAsync(header.AsMemory(length), _stop.Token)
                            .ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw new EndOfStreamException();
                        }
                        length += count;
                    }
                    const int HeaderTerminatorBytes = 4;
                    bodyStart += HeaderTerminatorBytes;
                    string headers = Encoding.ASCII.GetString(header, 0, bodyStart);
                    int bodyBytes = 0;
                    foreach (
                        string line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    )
                    {
                        const string ContentLength = "Content-Length:";
                        if (line.StartsWith(ContentLength, StringComparison.OrdinalIgnoreCase))
                        {
                            bodyBytes = int.Parse(
                                line.AsSpan(ContentLength.Length),
                                System.Globalization.CultureInfo.InvariantCulture
                            );
                        }
                    }
                    Assert.InRange(bodyBytes, 0, header.Length - bodyStart);
                    while (length < bodyStart + bodyBytes)
                    {
                        int count = await stream
                            .ReadAsync(header.AsMemory(length), _stop.Token)
                            .ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw new EndOfStreamException();
                        }
                        length += count;
                    }
                    Assert.InRange(Interlocked.Increment(ref _requests), 1, MaximumRequests);
                    _requested.TrySetResult();
                    await _respond.Task.WaitAsync(_stop.Token).ConfigureAwait(false);
                    bool ocsp = headers.Contains("/ocsp", StringComparison.Ordinal);
                    var body =
                        _unavailable ? ReadOnlyMemory<byte>.Empty
                        : ocsp ? _ocsp
                        : _crl;
                    string status = _unavailable ? "503 Service Unavailable" : "200 OK";
                    byte[] response = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status}\r\nContent-Type: {(ocsp ? "application/ocsp-response" : "application/pkix-crl")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"
                    );
                    await stream.WriteAsync(response, _stop.Token).ConfigureAwait(false);
                    await stream.WriteAsync(body, _stop.Token).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // A cancelled platform fetch may abandon its HTTP connection.
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _server.ConfigureAwait(false);
        }
        finally
        {
            _listener.Dispose();
            _stop.Dispose();
            Server.Dispose();
            Root.Dispose();
        }
    }
}
