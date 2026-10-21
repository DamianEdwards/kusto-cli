using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class UpdateCheckProcessTests
{
    [Fact]
    public async Task NormalCommand_StartsBackgroundCheckAndPreservesStructuredOutput()
    {
        using var fixture = new ProcessFixture();
        var first = await fixture.RunAsync("cluster", "list", "--format", "json");
        Assert.Equal(0, first.ExitCode);
        using var firstJson = JsonDocument.Parse(first.Output);
        await fixture.WaitForCachedVersionAsync("9999.0.0");
        var timestamp = File.GetLastWriteTimeUtc(fixture.CachePath);

        var second = await fixture.RunAsync("cluster", "list", "--format", "json");
        Assert.Equal(0, second.ExitCode);
        using var secondJson = JsonDocument.Parse(second.Output);
        Assert.Equal(first.Output, second.Output);
        Assert.Equal(
            "A new version of kusto is available: 9999.0.0. Run 'kusto update' to install it.",
            second.Error.Trim());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.CachePath));
    }

    [Fact]
    public async Task WaitForCachedVersion_ReadsWithoutAcquiringWriterLock()
    {
        using var fixture = new ProcessFixture();
        var cache = new UpdateCheckCache(fixture.CachePath, NullLogger.Instance);
        using var lease = cache.TryAcquireLock();
        Assert.NotNull(lease);
        Assert.True(cache.Save("9999.0.0", DateTimeOffset.UtcNow));

        await fixture.WaitForCachedVersionAsync("9999.0.0");
    }

    [Fact]
    public async Task FailedCommand_PrintsReminderAfterErrorAndLogs()
    {
        using var fixture = new ProcessFixture();
        Assert.Equal(0, (await fixture.RunAsync("update", "--check")).ExitCode);

        var result = await fixture.RunAsync(
            "query", "print (", "--cluster", "https://help.kusto.windows.net",
            "--database", "Samples", "--log-level", "Error");
        Assert.Equal(1, result.ExitCode);
        Assert.EndsWith(
            "A new version of kusto is available: 9999.0.0. Run 'kusto update' to install it.",
            result.Error.Trim(), StringComparison.Ordinal);
        Assert.True(result.Error.Trim().Split('\n').Length > 1);
    }

    [Fact]
    public async Task BackgroundCheck_IsSilentThrottledAndExplicitCheckRefreshesCache()
    {
        using var fixture = new ProcessFixture();
        var first = await fixture.RunAsync("update", "--check", "--background");
        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.Output);
        Assert.Empty(first.Error);
        Assert.Equal("9999.0.0", File.ReadAllText(fixture.CachePath));

        fixture.SetRelease("9999.1.0");
        var second = await fixture.RunAsync("update", "--check", "--background");
        Assert.Equal(0, second.ExitCode);
        Assert.Empty(second.Output);
        Assert.Empty(second.Error);
        Assert.Equal("9999.0.0", File.ReadAllText(fixture.CachePath));

        var explicitCheck = await fixture.RunAsync("update", "--check");
        Assert.Equal(0, explicitCheck.ExitCode);
        Assert.Contains("9999.1.0", explicitCheck.Output);
        Assert.Empty(explicitCheck.Error);
        Assert.Equal("9999.1.0", File.ReadAllText(fixture.CachePath));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("completions", "script", "pwsh")]
    [InlineData("update", "--dry-run")]
    public async Task NonNormalCommands_DoNotStartBackgroundCheck(params string[] arguments)
    {
        using var fixture = new ProcessFixture();
        var result = await fixture.RunAsync(arguments);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.False(File.Exists(fixture.CachePath));
        Assert.False(File.Exists(fixture.CachePath + ".lock"));
    }

    [Fact]
    public async Task DisabledUpdates_DoNotReadOrWriteCacheOrPrintReminder()
    {
        using var fixture = new ProcessFixture { DisableUpdates = true };
        File.WriteAllText(fixture.CachePath, "9999.0.0");
        File.SetLastWriteTimeUtc(fixture.CachePath, DateTime.UtcNow.AddHours(-7));
        var timestamp = File.GetLastWriteTimeUtc(fixture.CachePath);
        var result = await fixture.RunAsync("cluster", "list", "--format", "json");
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(fixture.CachePath));
        Assert.False(File.Exists(fixture.CachePath + ".lock"));
    }

    [Fact]
    public async Task ConcurrentBackgroundProcesses_SkipHeldLockWithoutWaiting()
    {
        using var fixture = new ProcessFixture();
        var cache = new UpdateCheckCache(fixture.CachePath, NullLogger.Instance);
        using var lease = cache.TryAcquireLock();
        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => fixture.RunAsync("update", "--check", "--background")));
        Assert.All(results, result =>
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Empty(result.Error);
        });
        Assert.False(File.Exists(fixture.CachePath));
    }

    private sealed class ProcessFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), $"kusto-check-process-{Guid.NewGuid():N}");

        public ProcessFixture()
        {
            Directory.CreateDirectory(_root);
            SetRelease("9999.0.0");
        }

        public string CachePath => Path.Combine(_root, "update-check");
        public bool DisableUpdates { get; init; }

        public void SetRelease(string version)
            => File.WriteAllText(
                Path.Combine(_root, "release-metadata.json"),
                $"{{\"version\":\"{version}\"}}");

        public async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] arguments)
        {
            var info = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, AppIdentity.GetExecutableFileName()),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }
            info.Environment[AppIdentity.HomeEnvVar] = _root;
            info.Environment["KUSTO_CONFIG_PATH"] = Path.Combine(_root, "config.json");
            info.Environment[AppIdentity.UpdateSourceEnvVar] = _root;
            info.Environment[AppIdentity.DisableSelfUpdatesEnvVar] = DisableUpdates ? "1" : "0";
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                return (process.ExitCode, await output.WaitAsync(timeout.Token), await error.WaitAsync(timeout.Token));
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync();
                throw;
            }
        }

        public async Task WaitForCachedVersionAsync(string version)
        {
            var cache = new UpdateCheckCache(CachePath, NullLogger.Instance);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                // Cache writes are atomic; taking the writer lock can make the background check skip.
                if (cache.ReadVersion() == version)
                {
                    return;
                }
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
