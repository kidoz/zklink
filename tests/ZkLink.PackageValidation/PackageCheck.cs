using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace ZkLink.PackageValidation;

internal static class PackageCheck
{
    private const string AssemblyAsset = "lib/net10.0/ZkLink.dll";
    private const string Repository = "https://github.com/kidoz/zklink";
    private const int MaximumMemberBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    internal static void Run(
        string package,
        string symbols,
        string version,
        string revision,
        string report
    )
    {
        using var archive = ZipFile.OpenRead(package);
        using var symbolArchive = ZipFile.OpenRead(symbols);
        CheckEntries(
            archive,
            [
                "ZkLink.nuspec",
                "LICENSE",
                "README.md",
                "THIRD-PARTY-NOTICES.md",
                AssemblyAsset,
                "lib/net10.0/ZkLink.xml",
            ]
        );
        CheckEntries(symbolArchive, ["ZkLink.nuspec", "lib/net10.0/ZkLink.pdb"]);
        CheckMetadata(archive, version, revision, symbols: false);
        CheckMetadata(symbolArchive, version, revision, symbols: true);
        using var assembly = new MemoryStream(Read(archive, AssemblyAsset), writable: false);
        using var pe = new PEReader(assembly);
        var metadata = pe.GetMetadataReader();
        Require(metadata.GetString(metadata.GetAssemblyDefinition().Name) == "ZkLink");
        var frameworkAssemblies = Directory
            .GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);
        var references = metadata
            .AssemblyReferences.Select(handle =>
                metadata.GetString(metadata.GetAssemblyReference(handle).Name)
            )
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(references.All(frameworkAssemblies.Contains));
        var sourceReport = SymbolCheck.Run(
            pe,
            Read(symbolArchive, "lib/net10.0/ZkLink.pdb"),
            revision
        );
        var result = new JsonObject
        {
            ["packageId"] = "ZkLink",
            ["version"] = version,
            ["revision"] = revision,
            ["packageSha256"] = HashFile(package),
            ["symbolsSha256"] = HashFile(symbols),
            ["assemblySha256"] = Convert.ToHexString(SHA256.HashData(assembly.ToArray())),
            ["assets"] = new JsonArray(
                archive.Entries.Select(entry => JsonValue.Create(entry.FullName)).ToArray()
            ),
            ["assemblyReferences"] = new JsonArray(
                references.Select(value => JsonValue.Create(value)).ToArray()
            ),
            ["packageDependencyCount"] = 0,
            ["symbols"] = sourceReport,
        };
        File.WriteAllText(report, result.ToJsonString(ReportOptions) + Environment.NewLine);
    }

    internal static void Consumer(string assetsPath, string assemblyPath, string reportPath)
    {
        using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
        using var report = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        var expected = report.RootElement;
        var key = $"ZkLink/{expected.GetProperty("version").GetString()}";
        var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject().ToArray();
        Require(
            libraries is [{ Name: var name, Value: var library }]
                && name == key
                && library.GetProperty("type").GetString() == "package"
        );
        var targets = assets.RootElement.GetProperty("targets").EnumerateObject().ToArray();
        Require(targets is [{ Name: "net10.0" }]);
        var target = targets[0].Value.GetProperty(key);
        foreach (var kind in new[] { "compile", "runtime" })
        {
            var entries = target.GetProperty(kind).EnumerateObject().ToArray();
            Require(entries is [{ Name: AssemblyAsset }]);
        }

        Require(HashFile(assemblyPath) == expected.GetProperty("assemblySha256").GetString());
    }

    internal static void Require(bool condition)
    {
        if (!condition)
        {
            throw new InvalidDataException("Package contract was not satisfied.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void CheckEntries(ZipArchive archive, string[] assets)
    {
        var expected = assets
            .Concat([
                "_rels/.rels",
                "[Content_Types].xml",
                "package/services/metadata/core-properties/nuget.psmdcp",
            ])
            .ToHashSet(StringComparer.Ordinal);
        Require(archive.Entries.Count == expected.Count);
        foreach (var entry in archive.Entries)
        {
            Require(expected.Remove(entry.FullName));
            Require(entry.Length is > 0 and <= MaximumMemberBytes);
        }

        Require(expected.Count == 0);
    }

    private static void CheckMetadata(
        ZipArchive archive,
        string version,
        string revision,
        bool symbols
    )
    {
        using var content = new MemoryStream(Read(archive, "ZkLink.nuspec"), writable: false);
        var root = XElement.Load(content);
        XNamespace ns = root.Name.Namespace;
        var metadata = root.Element(ns + "metadata") ?? throw new InvalidDataException();
        Require(metadata.Element(ns + "id")?.Value == "ZkLink");
        Require(metadata.Element(ns + "version")?.Value == version);
        Require(metadata.Element(ns + "projectUrl")?.Value == Repository);
        var repository = metadata.Element(ns + "repository");
        Require(
            repository?.Attribute("type")?.Value == "git"
                && repository.Attribute("url")?.Value == Repository
                && repository.Attribute("commit")?.Value == revision
        );
        var dependencies = metadata.Element(ns + "dependencies");
        Require(dependencies is not null && !dependencies.Descendants(ns + "dependency").Any());
        Require(
            dependencies!.Elements(ns + "group").Single().Attribute("targetFramework")?.Value
                == "net10.0"
        );
        if (symbols)
        {
            Require(
                metadata
                    .Element(ns + "packageTypes")
                    ?.Element(ns + "packageType")
                    ?.Attribute("name")
                    ?.Value == "SymbolsPackage"
            );
        }
        else
        {
            Require(metadata.Element(ns + "authors")?.Value == "Aleksandr Pavlov");
            Require(
                metadata.Element(ns + "license")?.Value == "MIT"
                    && metadata.Element(ns + "license")?.Attribute("type")?.Value == "expression"
            );
            Require(metadata.Element(ns + "readme")?.Value == "README.md");
            using var xml = new MemoryStream(
                Read(archive, "lib/net10.0/ZkLink.xml"),
                writable: false
            );
            var documentation = XElement.Load(xml);
            Require(
                documentation.Element("assembly")?.Element("name")?.Value == "ZkLink"
                    && documentation.Element("members")?.Elements().Any() == true
            );
        }
    }

    private static byte[] Read(ZipArchive archive, string name)
    {
        using var input = archive.GetEntry(name)!.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
