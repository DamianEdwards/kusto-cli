using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class ProvenanceVerifierTests
{
    [Fact]
    public void VerifyChecksum_AcceptsMatchingHash()
    {
        using var fixture = new ProvenanceFixture();

        ProvenanceVerifier.VerifyChecksum(
            fixture.ArchivePath,
            fixture.ChecksumsPath,
            fixture.AssetName);
    }

    [Fact]
    public void VerifyChecksum_RejectsMismatch()
    {
        using var fixture = new ProvenanceFixture();
        File.WriteAllText(
            fixture.ChecksumsPath,
            $"{new string('0', 64)}  {fixture.AssetName}");

        var exception = Assert.Throws<UserFacingException>(
            () => ProvenanceVerifier.VerifyChecksum(
                fixture.ArchivePath,
                fixture.ChecksumsPath,
                fixture.AssetName));

        Assert.Contains("SHA256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReleaseMetadata_RejectsDifferentHash()
    {
        using var fixture = new ProvenanceFixture();
        var metadataPath = Path.Combine(fixture.Root, "release-metadata.json");
        File.WriteAllText(metadataPath, $$"""
            {
              "version": "1.2.3",
              "assets": [
                {
                  "name": "{{fixture.AssetName}}",
                  "sha256": "{{new string('0', 64)}}"
                }
              ]
            }
            """);

        var exception = Assert.Throws<UserFacingException>(
            () => ProvenanceVerifier.ValidateReleaseMetadata(
                metadataPath,
                fixture.AssetName,
                fixture.Hash,
                "1.2.3"));

        Assert.Contains("does not agree", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetExpectedSha256_RejectsMalformedEntry()
    {
        using var fixture = new ProvenanceFixture();
        File.WriteAllText(fixture.ChecksumsPath, $"not-a-hash  {fixture.AssetName}");

        var exception = Assert.Throws<UserFacingException>(
            () => ProvenanceVerifier.GetExpectedSha256(
                fixture.ChecksumsPath,
                fixture.AssetName));

        Assert.Contains("valid SHA256", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetWindowsPowerShellModulePath_UsesWindowsPowerShellLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = ProvenanceVerifier.GetWindowsPowerShellModulePath()
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains(
            paths,
            path => path.EndsWith(
                @"WindowsPowerShell\v1.0\Modules",
                StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            paths,
            path => path.Contains(
                @"PowerShell\7\Modules",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            paths.Length,
            paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GetWindowsExecutablePayloadFileNames_SelectsEveryExeAndDll()
    {
        var files = ProvenanceVerifier.GetWindowsExecutablePayloadFileNames(
        [
            "kusto.exe",
            "libSkiaSharp.dll",
            "native/msalruntime.dll",
            "LICENSE",
            "data/settings.json"
        ]);

        Assert.Equal(
        [
            "kusto.exe",
            "libSkiaSharp.dll",
            "native/msalruntime.dll"
        ], files);
    }

    [Fact]
    public void MacOSVerifier_EmbedsTheExactInstallerTrustPolicy()
    {
        var embedded = ProvenanceVerifier.ReadEmbeddedVerificationScript(
            "verify-macos-provenance.sh",
            "macOS signature");
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var installer = Path.Combine(directory, "scripts", "install", "install-kusto-cli.sh");
            if (File.Exists(installer))
            {
                Assert.Equal(File.ReadAllText(installer), embedded);
                Assert.Contains("MACOS_SIGNING_TEAM_ID=\"7B8Z7H3R6G\"", embedded, StringComparison.Ordinal);
                return;
            }
            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("Could not locate the Unix installer trust policy.");
    }

    [Fact]
    public async Task VerifyMacOSPayloadAsync_RejectsUnsignedNativePayloadBeforeExecution()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var fixture = new ProvenanceFixture();
        var files = new[] { "kusto", "libHarfBuzzSharp.dylib", "libSkiaSharp.dylib", "libsodium.dylib" };
        foreach (var file in files)
        {
            File.WriteAllBytes(Path.Combine(fixture.Root, file), [0xcf, 0xfa, 0xed, 0xfe, 0, 0, 0, 0]);
        }
        File.Delete(fixture.ArchivePath);
        File.Delete(fixture.ChecksumsPath);
        File.WriteAllText(
            Path.Combine(fixture.Root, "payload-manifest.json"),
            JsonSerializer.Serialize(new { files }));
        var verifier = new ProvenanceVerifier(NullLogger<ProvenanceVerifier>.Instance);

        var exception = await Assert.ThrowsAsync<UserFacingException>(
            () => verifier.VerifyMacOSPayloadAsync(fixture.Root, CancellationToken.None));

        Assert.Contains("macOS signature verification failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("7B8Z7H3R6G", exception.Message, StringComparison.Ordinal);
    }

    private sealed class ProvenanceFixture : IDisposable
    {
        public ProvenanceFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"kusto-provenance-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            AssetName = "kusto-test.zip";
            ArchivePath = Path.Combine(Root, AssetName);
            ChecksumsPath = Path.Combine(Root, "checksums.txt");
            File.WriteAllText(ArchivePath, "trusted archive bytes");
            Hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ArchivePath)));
            File.WriteAllText(ChecksumsPath, $"{Hash}  {AssetName}");
        }

        public string Root { get; }
        public string AssetName { get; }
        public string ArchivePath { get; }
        public string ChecksumsPath { get; }
        public string Hash { get; }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
