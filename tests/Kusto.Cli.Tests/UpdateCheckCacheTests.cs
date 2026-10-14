using Microsoft.Extensions.Logging.Abstractions;

namespace Kusto.Cli.Tests;

public sealed class UpdateCheckCacheTests
{
    [Fact]
    public void IsDue_UsesSixHourBoundaryAndRecoversFromFutureTimestamp()
    {
        using var fixture = new CacheFixture();
        var now = DateTimeOffset.UtcNow;
        Assert.True(fixture.Cache.IsDue(now));

        using var lease = fixture.Cache.TryAcquireLock();
        Assert.NotNull(lease);
        Assert.True(fixture.Cache.Save("2.0.0", now));
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(fixture.Path));
        Assert.False(fixture.Cache.IsDue(written.AddHours(6).AddTicks(-1)));
        Assert.True(fixture.Cache.IsDue(written.AddHours(6)));
        Assert.True(fixture.Cache.IsDue(written.AddSeconds(-1)));
        Assert.Equal("2.0.0", fixture.Cache.ReadVersion());
    }

    [Fact]
    public async Task CheckAsync_ThrottlesBackgroundButNotExplicitChecks()
    {
        using var fixture = new CacheFixture();
        var calls = 0;
        Task<UpdateCheckResult?> Check(CancellationToken _)
        {
            calls++;
            return Task.FromResult<UpdateCheckResult?>(NewUpdate);
        }

        Assert.Equal(NewUpdate, await fixture.Cache.CheckAsync(Check, true, default));
        Assert.Null(await fixture.Cache.CheckAsync(Check, true, default));
        Assert.Equal(1, calls);
        Assert.Equal(NewUpdate, await fixture.Cache.CheckAsync(Check, false, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CheckAsync_ConcurrentBackgroundChecksDoNotWaitOrCallServer()
    {
        using var fixture = new CacheFixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.Cache.CheckAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
            return NewUpdate;
        }, true, default);
        await started.Task;

        try
        {
            var secondCache = new UpdateCheckCache(fixture.Path, NullLogger.Instance);
            var second = secondCache.CheckAsync(
                _ => throw new InvalidOperationException("Must not check concurrently"),
                true, default);
            Assert.True(second.IsCompletedSuccessfully);
            Assert.Null(await second);
            Assert.Null(secondCache.TryAcquireLock());
            Assert.Null(secondCache.ReadVersion());
            secondCache.Clear();
            Assert.True(File.Exists(fixture.Path));
        }
        finally
        {
            release.SetResult();
            await first;
        }

        Assert.Equal("2.0.0", fixture.Cache.ReadVersion());
        using var nextLease = fixture.Cache.TryAcquireLock();
        Assert.NotNull(nextLease);
    }

    [Fact]
    public async Task CheckAsync_FailureRetainsVersionAndThrottlesRetry()
    {
        using var fixture = new CacheFixture();
        using (fixture.Cache.TryAcquireLock())
        {
            fixture.Cache.Save("2.0.0", DateTimeOffset.UtcNow.AddHours(-7));
        }

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Cache.CheckAsync(
            _ => throw new HttpRequestException("Offline"), true, default));
        Assert.Equal("2.0.0", fixture.Cache.ReadVersion());
        Assert.False(fixture.Cache.IsDue(DateTimeOffset.UtcNow));
        using var lease = fixture.Cache.TryAcquireLock();
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task CheckAsync_CancellationReleasesLockAndThrottlesRetry()
    {
        using var fixture = new CacheFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Cache.CheckAsync(
            ct => Task.FromCanceled<UpdateCheckResult?>(ct), true, cancellation.Token));
        Assert.False(fixture.Cache.IsDue(DateTimeOffset.UtcNow));
        using var lease = fixture.Cache.TryAcquireLock();
        Assert.NotNull(lease);
    }

    [Fact]
    public void ReadVersion_ExclusiveFileAccessDoesNotWaitOrThrow()
    {
        using var fixture = new CacheFixture();
        using var lease = fixture.Cache.TryAcquireLock();
        fixture.Cache.Save("2.0.0", DateTimeOffset.UtcNow);
        using var stream = new FileStream(
            fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(fixture.Cache.ReadVersion());
    }

    [Fact]
    public async Task CheckAsync_NoUpdateClearsKnownVersion()
    {
        using var fixture = new CacheFixture();
        using (fixture.Cache.TryAcquireLock())
        {
            fixture.Cache.Save("2.0.0", DateTimeOffset.UtcNow.AddHours(-7));
        }

        await fixture.Cache.CheckAsync(_ => Task.FromResult<UpdateCheckResult?>(null), true, default);
        Assert.Equal("", File.ReadAllText(fixture.Path));
        Assert.Null(fixture.Cache.ReadVersion());
        Assert.False(fixture.Cache.IsDue(DateTimeOffset.UtcNow));
        fixture.Cache.Clear();
        Assert.False(File.Exists(fixture.Path));
        Assert.True(fixture.Cache.IsDue(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task CheckAsync_ExplicitCheckDoesNotWaitForBusyCache()
    {
        using var fixture = new CacheFixture();
        using var lease = fixture.Cache.TryAcquireLock();
        var other = new UpdateCheckCache(fixture.Path, NullLogger.Instance);
        Assert.Equal(NewUpdate, await other.CheckAsync(
            _ => Task.FromResult<UpdateCheckResult?>(NewUpdate), false, default));
        Assert.False(File.Exists(fixture.Path));
    }

    [Fact]
    public async Task InaccessibleCache_DoesNotThrowOrCheckInBackground()
    {
        using var fixture = new CacheFixture();
        Directory.CreateDirectory(fixture.Path);
        Assert.Null(fixture.Cache.ReadVersion());
        Assert.False(fixture.Cache.Save("2.0.0", DateTimeOffset.UtcNow));
        fixture.Cache.Clear();
        Assert.Null(await fixture.Cache.CheckAsync(
            _ => throw new InvalidOperationException("Must not check with inaccessible cache"),
            true, default));
    }

    [Fact]
    public void MalformedCache_IsIgnored()
    {
        using var fixture = new CacheFixture();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Path)!);
        File.WriteAllText(fixture.Path, "not a version\nRun something");
        Assert.Null(fixture.Cache.ReadVersion());
    }

    [Fact]
    public async Task ReadersObserveOnlyCompleteVersionsDuringAtomicReplacement()
    {
        using var fixture = new CacheFixture();
        using var lease = fixture.Cache.TryAcquireLock();
        fixture.Cache.Save("2.0.0", DateTimeOffset.UtcNow);
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                var version = fixture.Cache.ReadVersion();
                Assert.True(version is "2.0.0" or "3.0.0" or null);
            }
        });
        for (var i = 0; i < 200; i++)
        {
            fixture.Cache.Save(i % 2 == 0 ? "3.0.0" : "2.0.0", DateTimeOffset.UtcNow);
        }
        await reader;
    }

    private static UpdateCheckResult NewUpdate => new("1.0.0", "2.0.0", "v2.0.0", false);

    private sealed class CacheFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"kusto-check-test-{Guid.NewGuid():N}");

        public CacheFixture()
        {
            Path = System.IO.Path.Combine(_root, "update-check");
            Cache = new UpdateCheckCache(Path, NullLogger.Instance);
        }

        public string Path { get; }
        public UpdateCheckCache Cache { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
