using Director.Data;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class ProjectGenerationGuardTests
{
    [Fact]
    public async Task ProjectBusy_StopsBeforeDatabaseOllamaAndGpu()
    {
        var events = new List<string>();
        var db = new RecordingDbFactory(events);
        var ollama = new RecordingOllamaClient(events);
        var gpu = new RecordingGpuCoordinator(events);
        var project = new RecordingProjectCoordinator(events, busy: true);
        var service = CreateService(db, ollama, gpu, project);

        var exception = await Assert.ThrowsAsync<ProjectGenerationAlreadyRunningException>(() =>
            service.GenerateNextMissingSceneAsync(9));

        Assert.Equal(ProjectGenerationAlreadyRunningException.UserMessage, exception.Message);
        Assert.Equal(new[] { "Project" }, events);
        Assert.Equal(0, db.CallCount);
        Assert.Equal(0, ollama.CallCount);
        Assert.Equal(0, gpu.CallCount);
    }

    [Fact]
    public async Task SmokeBatchEntry_ProjectBusyStopsWholeBatchBeforeOtherWork()
    {
        var events = new List<string>();
        var db = new RecordingDbFactory(events);
        var ollama = new RecordingOllamaClient(events);
        var gpu = new RecordingGpuCoordinator(events);
        var project = new RecordingProjectCoordinator(events, busy: true);
        var service = CreateService(db, ollama, gpu, project);

        await Assert.ThrowsAsync<ProjectGenerationAlreadyRunningException>(() =>
            service.GenerateUpToMissingScenesAsync(9, 3));

        Assert.Equal(new[] { "Project" }, events);
        Assert.Equal(0, db.CallCount);
        Assert.Equal(0, ollama.CallCount);
        Assert.Equal(0, gpu.CallCount);
    }

    [Fact]
    public async Task StoryGeneration_AcquiresProjectBeforeDatabaseAndGpu()
    {
        var events = new List<string>();
        var db = new RecordingDbFactory(events);
        var service = CreateService(
            db,
            new RecordingOllamaClient(events),
            new RecordingGpuCoordinator(events),
            new RecordingProjectCoordinator(events, busy: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateNextMissingSceneAsync(9));

        Assert.Equal(new[] { "Project", "Database", "ProjectReleased" }, events);
    }

    [Theory]
    [InlineData(2601)]
    [InlineData(2627)]
    public void UniqueClassifier_AcceptsOnlyFilmProjectSceneNumberConstraint(int errorNumber)
    {
        Assert.True(StoryGenerationService.IsFilmSceneProjectSceneNumberUniqueViolation(
            errorNumber,
            "Violation of UNIQUE KEY constraint 'IX_FilmScenes_FilmProjectId_SceneNumber'."));
    }

    [Theory]
    [InlineData(2601, "IX_Other")]
    [InlineData(2627, "PK_FilmScenes")]
    [InlineData(1205, "IX_FilmScenes_FilmProjectId_SceneNumber")]
    public void UniqueClassifier_DoesNotSwallowOtherDatabaseErrors(int errorNumber, string message)
    {
        Assert.False(StoryGenerationService.IsFilmSceneProjectSceneNumberUniqueViolation(errorNumber, message));
    }

    [Fact]
    public void UniqueViolationReload_ClassifiesOnlyRequestedSceneAsConcurrentCompletion()
    {
        var completion = StoryGenerationService.ClassifyConcurrentSceneCompletion([15], [1, 2, 15]);

        Assert.Equal(new[] { 15 }, completion);
    }

    [Fact]
    public void UniqueViolationReload_DoesNotInventCompletionWhenRequestedSceneIsAbsent()
    {
        var completion = StoryGenerationService.ClassifyConcurrentSceneCompletion([15], [1, 2, 14]);

        Assert.Empty(completion);
    }

    private static StoryGenerationService CreateService(
        IDbContextFactory<AppDbContext> db,
        IOllamaClient ollama,
        IGpuGenerationCoordinator gpu,
        IProjectGenerationLeaseCoordinator project) =>
        new(
            db,
            ollama,
            null!,
            gpu,
            project,
            null!,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            NullLogger<StoryGenerationService>.Instance);

    private sealed class RecordingProjectCoordinator(List<string> events, bool busy) : IProjectGenerationLeaseCoordinator
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(int filmProjectId, CancellationToken cancellationToken = default)
        {
            events.Add("Project");
            if (busy)
            {
                throw new ProjectGenerationAlreadyRunningException(filmProjectId, "test");
            }

            return ValueTask.FromResult<IAsyncDisposable>(new CallbackLease(() => events.Add("ProjectReleased")));
        }
    }

    private sealed class RecordingDbFactory(List<string> events) : IDbContextFactory<AppDbContext>
    {
        public int CallCount { get; private set; }

        public AppDbContext CreateDbContext()
        {
            CallCount++;
            events.Add("Database");
            throw new InvalidOperationException("test database access stopped");
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            events.Add("Database");
            throw new InvalidOperationException("test database access stopped");
        }
    }

    private sealed class RecordingGpuCoordinator(List<string> events) : IGpuGenerationCoordinator
    {
        public int CallCount { get; private set; }
        public bool IsBusy => false;

        public Task<IAsyncDisposable> AcquireAsync(
            GenerationOperationType operationType,
            int projectId,
            int sceneId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            events.Add("Gpu");
            return Task.FromResult<IAsyncDisposable>(new CallbackLease(() => { }));
        }
    }

    private sealed class RecordingOllamaClient(List<string> events) : IOllamaClient
    {
        public int CallCount { get; private set; }

        public Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            Record();
            return Task.FromResult(new OllamaHealthResult { IsAvailable = true });
        }

        public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default)
        {
            Record();
            return Task.FromResult(true);
        }

        public Task<TResponse> ChatStructuredAsync<TResponse>(
            IReadOnlyList<OllamaChatMessage> messages,
            object jsonSchema,
            string? modelOverride = null,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default,
            IProgress<OllamaStreamProgress>? streamProgress = null,
            OllamaGenerationSettings? generationSettings = null)
        {
            Record();
            throw new InvalidOperationException("unexpected Ollama call");
        }

        public Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(
            IReadOnlyList<OllamaChatMessage> messages,
            object jsonSchema,
            string? modelOverride = null,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default,
            IProgress<OllamaStreamProgress>? streamProgress = null,
            OllamaGenerationSettings? generationSettings = null)
        {
            Record();
            throw new InvalidOperationException("unexpected Ollama call");
        }

        private void Record()
        {
            CallCount++;
            events.Add("Ollama");
        }
    }

    private sealed class CallbackLease(Action release) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
