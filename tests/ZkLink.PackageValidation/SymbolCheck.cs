using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZkLink.PackageValidation;

internal static class SymbolCheck
{
    private const int MaximumSourceBytes = 4 * 1024 * 1024;
    private const int PortablePdbAge = 1;
    private static readonly Guid SourceLinkKind = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");
    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid Sha256Kind = new("8829D00F-11B8-4213-878B-770E8597AC16");

    internal static JsonObject Run(PEReader pe, byte[] pdb, string revision)
    {
        using var stream = new MemoryStream(pdb, writable: false);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var id = new BlobContentId(reader.DebugMetadataHeader!.Id);
        var debugEntry = pe.ReadDebugDirectory()
            .Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        var codeView = pe.ReadCodeViewDebugDirectoryData(debugEntry);
        PackageCheck.Require(
            codeView.Guid == id.Guid
                && codeView.Age == PortablePdbAge
                && debugEntry.Stamp == id.Stamp
        );
        var sourceLink = reader
            .CustomDebugInformation.Select(reader.GetCustomDebugInformation)
            .Single(info => reader.GetGuid(info.Kind) == SourceLinkKind);
        PackageCheck.Require(sourceLink.Parent.Kind == HandleKind.ModuleDefinition);
        using var json = JsonDocument.Parse(reader.GetBlobBytes(sourceLink.Value));
        var maps = json.RootElement.GetProperty("documents").EnumerateObject().ToArray();
        var expectedUrl = $"https://raw.githubusercontent.com/kidoz/zklink/{revision}/*";
        PackageCheck.Require(
            maps is [{ Name: "/_/*" }] && maps[0].Value.GetString() == expectedUrl
        );
        var documents = new JsonArray();
        foreach (var handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            var name = reader.GetString(document.Name);
            PackageCheck.Require(name.StartsWith("/_/", StringComparison.Ordinal));
            PackageCheck.Require(reader.GetGuid(document.HashAlgorithm) == Sha256Kind);
            var embedded = reader
                .GetCustomDebugInformation(handle)
                .Select(reader.GetCustomDebugInformation)
                .Single(info => reader.GetGuid(info.Kind) == EmbeddedSourceKind);
            var bytes = Decode(reader.GetBlobBytes(embedded.Value));
            var hash = SHA256.HashData(bytes);
            PackageCheck.Require(hash.AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)));
            documents.Add(
                new JsonObject { ["name"] = name, ["sha256"] = Convert.ToHexString(hash) }
            );
        }

        PackageCheck.Require(documents.Count > 0);
        // Portable PDB checksums cover the file with its ID bytes zeroed (PE/COFF addendum).
        var checksumEntry = pe.ReadDebugDirectory()
            .Single(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum);
        var checksum = pe.ReadPdbChecksumDebugDirectoryData(checksumEntry);
        PackageCheck.Require(checksum.AlgorithmName == "SHA256");
        var header = reader.DebugMetadataHeader;
        var zeroed = pdb.ToArray();
        zeroed.AsSpan(header.IdStartOffset, header.Id.Length).Clear();
        PackageCheck.Require(
            SHA256.HashData(zeroed).AsSpan().SequenceEqual(checksum.Checksum.AsSpan())
        );
        return new JsonObject
        {
            ["pdbMatchesAssembly"] = true,
            ["pdbChecksumVerified"] = true,
            ["sourceLinkUrl"] = expectedUrl,
            ["embeddedSources"] = documents,
        };
    }

    private static byte[] Decode(byte[] blob)
    {
        PackageCheck.Require(blob.Length >= sizeof(int));
        var length = BinaryPrimitives.ReadInt32LittleEndian(blob);
        PackageCheck.Require(length is >= 0 and <= MaximumSourceBytes);
        if (length == 0)
        {
            PackageCheck.Require(blob.Length - sizeof(int) <= MaximumSourceBytes);
            return blob[sizeof(int)..];
        }

        using var compressed = new MemoryStream(
            blob,
            sizeof(int),
            blob.Length - sizeof(int),
            writable: false
        );
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        var output = new byte[length];
        deflate.ReadExactly(output);
        PackageCheck.Require(deflate.ReadByte() == -1);
        return output;
    }
}
