using Microsoft.CodeAnalysis;

namespace ZkLink.Analyzers.Tests;

public sealed class SecurityAnalyzerTests
{
    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task LiteralPasswordsIncludeConstantsAndNamedArgumentsWithoutLeakingValues()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using ZkLink;
            public static class Example
            {
                private const string SavedSecret = "constant-secret";
                public static void Run(string supplied)
                {
                    _ = ZooKeeperAuthentication.Digest("user", "inline-secret");
                    _ = ZooKeeperAuthentication.Digest("user", SavedSecret);
                    _ = ZooKeeperAuthentication.Digest(password: "named-secret", userName: "user");
                    _ = ZooKeeperAuthentication.Digest("user", supplied);
                    _ = ZooKeeperAuthentication.Digest("user", "");
                    _ = Other.Digest("user", "inline-secret");
                }
            }
            public static class Other
            {
                public static string Digest(string userName, string password) => password;
            }
            """,
            new LiteralDigestPasswordAnalyzer()
        );

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0001", diagnostic.Id));
        Assert.All(
            diagnostics,
            diagnostic =>
            {
                var message = diagnostic.GetMessage(
                    System.Globalization.CultureInfo.InvariantCulture
                );
                Assert.DoesNotContain("inline-secret", message, StringComparison.Ordinal);
                Assert.DoesNotContain("constant-secret", message, StringComparison.Ordinal);
                Assert.DoesNotContain("named-secret", message, StringComparison.Ordinal);
            }
        );
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task LiteralPasswordAnalyzerSkipsGeneratedCode()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeGeneratedAsync(
            """
            using ZkLink;
            public static class Example
            {
                public static void Run() =>
                    _ = ZooKeeperAuthentication.Digest("user", "generated-secret");
            }
            """,
            new LiteralDigestPasswordAnalyzer()
        );

        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait("Acceptance", "T41")]
    public async Task UnsafePlaintextDigestRecognizesExplicitAndImplicitOptionsCreation()
    {
        var diagnostics = await AnalyzerTestHarness.AnalyzeAsync(
            """
            using ZkLink;
            public static class Example
            {
                private const bool Unsafe = true;
                public static void Run()
                {
                    var explicitOptions = new ZooKeeperClientOptions
                    {
                        ConnectionString = "localhost",
                        AllowUnsafePlaintextDigest = true
                    };
                    var constantOptions = new ZooKeeperClientOptions
                    {
                        ConnectionString = "localhost",
                        AllowUnsafePlaintextDigest = Unsafe
                    };
                    ZooKeeperClientOptions implicitOptions = new()
                    {
                        ConnectionString = "localhost",
                        AllowUnsafePlaintextDigest = true
                    };
                    var safe = new ZooKeeperClientOptions
                    {
                        ConnectionString = "localhost",
                        AllowUnsafePlaintextDigest = false
                    };
                    var unrelated = new OtherOptions { AllowUnsafePlaintextDigest = true };
                }
            }
            public sealed class OtherOptions
            {
                public bool AllowUnsafePlaintextDigest { get; init; }
            }
            """,
            new UnsafePlaintextDigestAnalyzer()
        );

        Assert.Equal(3, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal("ZKL0002", diagnostic.Id));
    }
}
