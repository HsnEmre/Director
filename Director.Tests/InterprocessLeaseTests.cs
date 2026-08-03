using System.Diagnostics;
using Director.Enums;
using Director.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Director.Tests;

public sealed class InterprocessLeaseTests
{
    [Fact]
    public async Task GpuLease_SecondAcquireWaitsUntilFirstRelease()
    {
        using var scope = new LockTestScope();
        var coordinator = scope.CreateGpuCoordinator();
        await using var first = await coordinator.AcquireAsync(GenerationOperationType.OllamaText, 1, 1);

        var secondTask = coordinator.AcquireAsync(GenerationOperationType.Video, 2, 2);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GpuLease_WaitCancellationDoesNotAcquireLater()
    {
        using var scope = new LockTestScope();
        var coordinator = scope.CreateGpuCoordinator();
        await using var first = await coordinator.AcquireAsync(GenerationOperationType.Image, 1, 1);
        using var cancellation = new CancellationTokenSource();
        var waiting = coordinator.AcquireAsync(GenerationOperationType.Video, 1, 1, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await first.DisposeAsync();
        await using var next = await coordinator.AcquireAsync(GenerationOperationType.Audio, 1, 1)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GpuLease_ExceptionScopeReleasesLease()
    {
        using var scope = new LockTestScope();
        var coordinator = scope.CreateGpuCoordinator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ThrowInsideLeaseAsync(coordinator));
        await using var next = await coordinator.AcquireAsync(GenerationOperationType.OllamaText, 1, 1)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GpuLease_ChildProcessBlocksParentAndNormalExitReleasesLock()
    {
        using var scope = new LockTestScope();
        using var child = await scope.StartProbeAsync("gpu", scope.Namespace);
        var parent = scope.CreateGpuCoordinator();
        var waiting = parent.AcquireAsync(GenerationOperationType.OllamaText, 1, 1);
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(250)));

        child.StandardInput.Close();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GpuLease_ForcedChildTerminationReleasesOsLock()
    {
        using var scope = new LockTestScope();
        using var child = await scope.StartProbeAsync("gpu", scope.Namespace);
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

        await using var lease = await scope.CreateGpuCoordinator()
            .AcquireAsync(GenerationOperationType.OllamaText, 1, 1)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GpuLease_StaleLockFileWithoutOsLockDoesNotBlock()
    {
        using var scope = new LockTestScope();
        Directory.CreateDirectory(scope.DirectoryPath);
        await File.WriteAllTextAsync(Path.Combine(scope.DirectoryPath, scope.Namespace + ".lock"), "stale");

        await using var lease = await scope.CreateGpuCoordinator()
            .AcquireAsync(GenerationOperationType.OllamaText, 1, 1)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GpuLease_CorruptMetadataIsToleratedAndOwnerMetadataContainsNoSecrets()
    {
        using var scope = new LockTestScope();
        var path = Path.Combine(scope.DirectoryPath, scope.Namespace + ".lock");
        Directory.CreateDirectory(scope.DirectoryPath);
        await File.WriteAllTextAsync(path, "\0{not-json Password=secret;User Id=admin");
        Assert.False(InterprocessFileLease.TryReadMetadata(path, out _));

        await using var lease = await scope.CreateGpuCoordinator().AcquireAsync(GenerationOperationType.Video, 42, 7);
        Assert.True(InterprocessFileLease.TryReadMetadata(path, out var metadata));
        var serialized = System.Text.Json.JsonSerializer.Serialize(metadata);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(42, metadata!.ProjectId);
    }

    [Fact]
    public async Task ProjectLease_SameDatabaseAndProjectReturnsControlledBusy()
    {
        using var scope = new LockTestScope();
        var firstCoordinator = scope.CreateProjectCoordinator("same-db");
        var secondCoordinator = scope.CreateProjectCoordinator("same-db");
        await using var first = await firstCoordinator.AcquireAsync(9);

        var exception = await Assert.ThrowsAsync<ProjectGenerationAlreadyRunningException>(async () =>
            await secondCoordinator.AcquireAsync(9));
        Assert.Equal(ProjectGenerationAlreadyRunningException.UserMessage, exception.Message);
    }

    [Fact]
    public async Task ProjectLease_DifferentProjectIdsDoNotBlock()
    {
        using var scope = new LockTestScope();
        var coordinator = scope.CreateProjectCoordinator("same-db");
        await using var first = await coordinator.AcquireAsync(9);
        await using var second = await coordinator.AcquireAsync(10);
    }

    [Fact]
    public async Task ProjectLease_DifferentDatabaseIdentitiesDoNotBlockSameProject()
    {
        using var scope = new LockTestScope();
        await using var first = await scope.CreateProjectCoordinator("db-a").AcquireAsync(9);
        await using var second = await scope.CreateProjectCoordinator("db-b").AcquireAsync(9);
    }

    [Fact]
    public async Task ProjectLease_ExceptionScopeReleasesLease()
    {
        using var scope = new LockTestScope();
        var coordinator = scope.CreateProjectCoordinator("same-db");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ThrowInsideProjectLeaseAsync(coordinator));
        await using var next = await coordinator.AcquireAsync(9);
    }

    [Fact]
    public async Task ProjectLease_CanceledAcquireDoesNotLeaveLease()
    {
        using var scope = new LockTestScope();
        var coordinator = scope.CreateProjectCoordinator("same-db");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await coordinator.AcquireAsync(9, cancellation.Token));
        await using var next = await coordinator.AcquireAsync(9);
    }

    [Fact]
    public async Task SameProjectConcurrentGeneration_AllowsOneModelCallAndOneInsert()
    {
        using var scope = new LockTestScope();
        var firstCoordinator = scope.CreateProjectCoordinator("same-db");
        var secondCoordinator = scope.CreateProjectCoordinator("same-db");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var modelCalls = 0;
        var inserts = 0;

        var first = Task.Run(async () =>
        {
            await using var lease = await firstCoordinator.AcquireAsync(9);
            Interlocked.Increment(ref modelCalls);
            Interlocked.Increment(ref inserts);
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<ProjectGenerationAlreadyRunningException>(async () =>
            await secondCoordinator.AcquireAsync(9));
        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, modelCalls);
        Assert.Equal(1, inserts);
        Assert.Equal(0, Math.Max(0, inserts - 1));
    }

    [Fact]
    public async Task FirstMissingScene_IsRecomputedAfterProjectLeaseAcquisition()
    {
        using var scope = new LockTestScope();
        var sceneNumbers = Enumerable.Range(1, 14).ToHashSet();
        var staleFirstMissing = StoryGenerationService.FindFirstMissingScene(sceneNumbers, 30);
        Assert.Equal(15, staleFirstMissing);

        sceneNumbers.Add(15); // Simulates another process completing scene 15 before this invocation acquires its lease.
        await using var lease = await scope.CreateProjectCoordinator("same-db").AcquireAsync(9);
        var freshFirstMissing = StoryGenerationService.FindFirstMissingScene(sceneNumbers, 30);

        Assert.Equal(16, freshFirstMissing);
    }

    [Fact]
    public async Task ProjectLease_ChildCrashReleasesOsLock()
    {
        using var scope = new LockTestScope();
        using var child = await scope.StartProbeAsync("project", "db-crash", "9");
        await Assert.ThrowsAsync<ProjectGenerationAlreadyRunningException>(async () =>
            await scope.CreateProjectCoordinator("db-crash").AcquireAsync(9));

        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await using var next = await scope.CreateProjectCoordinator("db-crash").AcquireAsync(9);
    }

    [Fact]
    public void DatabaseIdentity_IgnoresCredentialsAndSeparatesDatabases()
    {
        var first = DatabaseIdentity.Create(
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Server=server-a;Database=director;User Id=alice;Password=secret-one");
        var same = DatabaseIdentity.Create(
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Data Source=SERVER-A;Initial Catalog=DIRECTOR;User Id=bob;Password=secret-two");
        var other = DatabaseIdentity.Create(
            "Microsoft.EntityFrameworkCore.SqlServer",
            "Server=server-a;Database=other;User Id=alice;Password=secret-one");

        Assert.Equal(first.Hash, same.Hash);
        Assert.NotEqual(first.Hash, other.Hash);
        Assert.DoesNotContain("secret", first.Hash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, first.Hash.Length);
    }

    private static async Task ThrowInsideLeaseAsync(GpuGenerationCoordinator coordinator)
    {
        await using var lease = await coordinator.AcquireAsync(GenerationOperationType.OllamaText, 1, 1);
        throw new InvalidOperationException("expected");
    }

    private static async Task ThrowInsideProjectLeaseAsync(ProjectGenerationLeaseCoordinator coordinator)
    {
        await using var lease = await coordinator.AcquireAsync(9);
        throw new InvalidOperationException("expected");
    }

    private sealed class LockTestScope : IDisposable
    {
        public LockTestScope()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "DirectorLeaseTests", Guid.NewGuid().ToString("N"));
            Namespace = "Director.GpuLease.test." + Guid.NewGuid().ToString("N");
        }

        public string DirectoryPath { get; }
        public string Namespace { get; }

        public GpuGenerationCoordinator CreateGpuCoordinator() =>
            new(NullLogger<GpuGenerationCoordinator>.Instance, DirectoryPath, Namespace);

        public ProjectGenerationLeaseCoordinator CreateProjectCoordinator(string databaseHash) =>
            new(
                new DatabaseIdentity("test", databaseHash),
                NullLogger<ProjectGenerationLeaseCoordinator>.Instance,
                DirectoryPath);

        public async Task<Process> StartProbeAsync(params string[] probeArguments)
        {
            var assembly = typeof(InterprocessLockProbeMarker).Assembly.Location;
            var startInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(assembly);
            foreach (var argument in probeArguments.Take(1))
            {
                startInfo.ArgumentList.Add(argument);
            }
            startInfo.ArgumentList.Add(DirectoryPath);
            foreach (var argument in probeArguments.Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Probe process could not start.");
            try
            {
                var marker = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (marker != "READY")
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    throw new InvalidOperationException($"Probe did not become ready. Marker={marker}; Error={error}");
                }

                return process;
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                process.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
