using Director.Services;
using Director.Data;
using Director.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Director.Enums;

namespace Director.Tests;

public sealed class StoryResumeTests
{
    [Fact]
    public void Resume_WithZeroScenes_StartsAtFirstBatch()
    {
        var existing = new HashSet<int>();

        var firstMissing = StoryGenerationService.FindFirstMissingScene(existing, 30);

        Assert.Equal(1, firstMissing);
        Assert.Equal(1, StoryGenerationService.GetBatchStart(firstMissing));
    }

    [Fact]
    public void Resume_WithFiveScenes_StartsAtNextSingleScene()
    {
        var existing = Enumerable.Range(1, 5).ToHashSet();

        var firstMissing = StoryGenerationService.FindFirstMissingScene(existing, 30);

        Assert.Equal(6, firstMissing);
        Assert.Equal(6, StoryGenerationService.GetBatchStart(firstMissing));
    }

    [Fact]
    public void Resume_WithTenScenes_StartsAtNextSingleScene()
    {
        var existing = Enumerable.Range(1, 10).ToHashSet();

        var firstMissing = StoryGenerationService.FindFirstMissingScene(existing, 30);

        Assert.Equal(11, firstMissing);
        Assert.Equal(11, StoryGenerationService.GetBatchStart(firstMissing));
    }

    [Fact]
    public void Resume_WithPartialCheckpoint_StartsAtFirstMissingScene()
    {
        var existing = new HashSet<int> { 1, 2, 3 };

        var firstMissing = StoryGenerationService.FindFirstMissingScene(existing, 30);

        Assert.Equal(4, firstMissing);
        Assert.Equal(4, StoryGenerationService.GetBatchStart(firstMissing));
    }

    [Fact]
    public void TimeoutAfterSavedScene_PreservesCheckpointAndResumesAtNextScene()
    {
        var committedBeforeTimeout = new HashSet<int> { 1, 2 };

        var firstMissingAfterRestart = StoryGenerationService.FindFirstMissingScene(committedBeforeTimeout, 30);

        Assert.Equal(3, firstMissingAfterRestart);
    }

    [Fact]
    public void FilmSceneModel_PreventsDuplicateProjectSceneNumber()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=DirectorModelTest;Trusted_Connection=True")
            .Options;
        using var db = new AppDbContext(options);
        var entityType = db.Model.FindEntityType(typeof(FilmScene))!;
        var uniqueIndex = Assert.Single(entityType.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Select(property => property.Name).SequenceEqual(
                new[] { nameof(FilmScene.FilmProjectId), nameof(FilmScene.SceneNumber) }));

        Assert.True(uniqueIndex.IsUnique);
    }

    [Fact]
    public async Task GpuLock_IsReleasedWhenGenerationThrows()
    {
        var coordinator = CreateGpuCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ThrowWhileHoldingGpuLockAsync(coordinator));

        Assert.False(coordinator.IsBusy);
        await using var secondLease = await coordinator.AcquireAsync(GenerationOperationType.OllamaText, 9, 14);
        Assert.True(coordinator.IsBusy);
    }

    [Fact]
    public async Task GpuLock_CanceledBeforeAcquire_DoesNotMarkBusy()
    {
        var coordinator = CreateGpuCoordinator();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.AcquireAsync(GenerationOperationType.Image, 9, 1, cts.Token));

        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task GpuLock_WaitingAcquireCancellation_PreservesCurrentLease()
    {
        var coordinator = CreateGpuCoordinator();
        await using var firstLease = await coordinator.AcquireAsync(GenerationOperationType.Image, 9, 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.AcquireAsync(GenerationOperationType.Video, 9, 1, cts.Token));

        Assert.True(coordinator.IsBusy);
    }

    private static async Task ThrowWhileHoldingGpuLockAsync(GpuGenerationCoordinator coordinator)
    {
        await using var lease = await coordinator.AcquireAsync(GenerationOperationType.OllamaText, 9, 14);
        throw new InvalidOperationException("test failure");
    }

    private static GpuGenerationCoordinator CreateGpuCoordinator() =>
        new(
            NullLogger<GpuGenerationCoordinator>.Instance,
            Path.Combine(Path.GetTempPath(), "DirectorLeaseTests", Guid.NewGuid().ToString("N")),
            "Director.GpuLease.test." + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Completion_WithThirtyScenesAndThreeHundredSeconds_Passes()
    {
        var existing = Enumerable.Range(1, 30).ToHashSet();

        var error = StoryGenerationService.TryGetCompletionError(existing, 300, 30, 10);

        Assert.Null(error);
    }

    [Fact]
    public void Completion_WithMissingScene_Fails()
    {
        var existing = Enumerable.Range(1, 29).ToHashSet();

        var error = StoryGenerationService.TryGetCompletionError(existing, 290, 30, 10);

        Assert.NotNull(error);
    }

    [Fact]
    public void Completion_WithWrongDuration_Fails()
    {
        var existing = Enumerable.Range(1, 30).ToHashSet();

        var error = StoryGenerationService.TryGetCompletionError(existing, 290, 30, 10);

        Assert.Contains("Toplam sahne suresi", error);
    }
}
