using System.Text.Json;
using System.Xml;
using ZkLink.PackageValidation;

try
{
    if (args is ["package", var package, var symbols, var version, var revision, var report])
    {
        PackageCheck.Run(package, symbols, version, revision, report);
    }
    else if (args is ["consumer", var assets, var assembly, var manifest])
    {
        PackageCheck.Consumer(assets, assembly, manifest);
    }
    else if (
        args is ["symbols", var packagePath, var symbolsPath, var packageId, var source, var output]
    )
    {
        PackageCheck.Symbols(packagePath, symbolsPath, packageId, source, output);
    }
    else
    {
        Console.Error.WriteLine(
            "Expected package <nupkg> <snupkg> <version> <revision> <report>, consumer <assets> <assembly> <report>, or symbols <nupkg> <snupkg> <id> <revision> <report>."
        );
        return 2;
    }

    Console.Out.WriteLine("Package validation: PASS");
    return 0;
}
catch (Exception exception)
    when (exception
            is IOException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException
                or BadImageFormatException
                or JsonException
                or XmlException
    )
{
    // Do not print arbitrary XML, source text or local paths from parsing exceptions.
    Console.Error.WriteLine($"Package validation failed ({exception.GetType().Name}).");
    return 1;
}
