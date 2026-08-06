using Director.Dtos.Autonomous;
using Director.Dtos.MediaGeneration;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class AutonomousGenerationOrchestratorTests
{
    [Fact]
    public async Task RunAsync_CompletesPipeline_WithStopOnFailureRequests()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.Equal(1, storyService.GenerateMissingScenesCallCount);
        Assert.Equal(1, imageService.GenerateCallCount);
        Assert.True(imageService.LastRequest?.StopOnError);
        Assert.Equal(1, videoService.GenerateCallCount);
        Assert.True(videoService.LastRequest?.StopOnFailure);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.ImageStatus);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.VideoStatus);
        Assert.Equal(AutonomousWorkItemStatus.Skipped, runService.WorkItem.AudioStatus);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.FinalizationStatus);
    }

    [Fact]
    public async Task RunAsync_ResumeSkipsExistingValidAssets_AndKeepsIdempotency()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.SelectedImageAsset = files.CreateAsset(601, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        runService.SelectedVideoAsset = files.CreateAsset(602, MediaType.Video, MediaAssetRole.GeneratedSilentVideo, selected: true);
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, new FakeStoryGenerationService(), imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(0, videoService.GenerateCallCount);
        Assert.Equal(AutonomousWorkItemStatus.Skipped, runService.WorkItem.ImageStatus);
        Assert.Equal(AutonomousWorkItemStatus.Skipped, runService.WorkItem.VideoStatus);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task RunAsync_PausedRunDoesNotStartServices()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Paused;
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(AutonomousGenerationRunStatus.Paused, runService.Run.Status);
        Assert.Equal(0, storyService.GenerateMissingScenesCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(0, videoService.GenerateCallCount);
    }

    [Fact]
    public async Task RunAsync_CancelRequestedRunMarksCancelled()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.CancellationRequested = true;
        var storyService = new FakeStoryGenerationService();
        var orchestrator = CreateOrchestrator(runService, storyService, new FakeImageGenerationService(runService, files), new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(AutonomousGenerationRunStatus.Cancelled, runService.Run.Status);
        Assert.Equal(0, storyService.GenerateMissingScenesCallCount);
    }

    [Fact]
    public async Task RunAsync_ResumeFromGeneratingVideos_SkipsStoryAndCompletedImages()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        runService.Run.AttemptCount = 4;
        runService.WorkItem.ImageStatus = AutonomousWorkItemStatus.Completed;
        runService.SelectedImageAsset = files.CreateAsset(701, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, storyService.GenerateMissingScenesCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(1, videoService.GenerateCallCount);
        Assert.Equal(4, runService.Run.AttemptCount);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task RetryFailedRun_WithExistingStoryAndNoScenes_ReusesRunAndCreatesSingleScene()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Failed;
        runService.Run.CurrentStage = AutonomousGenerationStage.Failed;
        runService.Run.LastError = "Sahne 1 icin model cevabi dogrulanamadi.";
        runService.Scenes.Clear();
        runService.WorkItems.Clear();
        var originalRunId = runService.Run.Id;
        var storyService = new FakeStoryGenerationService
        {
            ExistingStoryCount = 1,
            CreateMissingScenes = projectId =>
            {
                if (runService.Scenes.Count == 0)
                {
                    runService.Scenes.Add(new FilmScene
                    {
                        Id = 111,
                        FilmProjectId = projectId,
                        SceneNumber = 1,
                        DurationSeconds = 5,
                        ImagePrompt = "image",
                        VideoPrompt = "video",
                        DialogueJson = "[]"
                    });
                }
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await runService.RetryAsync(originalRunId);
        await orchestrator.RunAsync(originalRunId);

        Assert.Equal(originalRunId, runService.Run.Id);
        Assert.Equal(1, storyService.ExistingStoryCount);
        Assert.Equal(0, storyService.StoryRegenerationCallCount);
        Assert.Equal(1, storyService.GenerateMissingScenesCallCount);
        Assert.Single(runService.Scenes);
        Assert.Single(runService.WorkItems);
        Assert.Equal(1, runService.Scenes[0].SceneNumber);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task TryClaimRunAsync_RejectsFreshOtherWorker_AndAcceptsStaleHeartbeat()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        runService.Run.WorkerId = "old-worker";
        runService.Run.LastHeartbeatAtUtc = DateTime.UtcNow;

        var freshClaim = await runService.TryClaimRunAsync(
            runService.Run.Id,
            "new-worker",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15));
        Assert.False(freshClaim);
        Assert.Equal("old-worker", runService.Run.WorkerId);

        runService.Run.LastHeartbeatAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(11);
        var staleClaim = await runService.TryClaimRunAsync(
            runService.Run.Id,
            "new-worker",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15));

        Assert.True(staleClaim);
        Assert.Equal("new-worker", runService.Run.WorkerId);
        Assert.Equal(AutonomousGenerationRunStatus.GeneratingVideos, runService.Run.Status);
        Assert.Equal(AutonomousGenerationStage.GeneratingVideos, runService.Run.CurrentStage);
    }

    [Fact]
    public async Task RunAsync_RenewsLeaseDuringLongImageGeneration_AndBlocksSecondWorkerClaim()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        Assert.True(await runService.TryClaimRunAsync(
            runService.Run.Id,
            "worker-1",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5)));
        var imageService = new FakeImageGenerationService(runService, files)
        {
            DelayBeforeCompleting = TimeSpan.FromSeconds(8)
        };
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(
            runService,
            new FakeStoryGenerationService(),
            imageService,
            videoService,
            new AutonomousGenerationOptions
            {
                HeartbeatIntervalSeconds = 1,
                LeaseExtensionSeconds = 5,
                StaleHeartbeatSeconds = 2
            });

        var runTask = orchestrator.RunAsync(runService.Run.Id, "worker-1");
        await imageService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var secondWorkerClaim = await runService.TryClaimRunAsync(
            runService.Run.Id,
            "worker-2",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(secondWorkerClaim);
        Assert.Equal(1, imageService.GenerateCallCount);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.Null(runService.Run.WorkerId);
    }

    [Fact]
    public async Task RetryPolicy_RetriesUntilSuccess()
    {
        var policy = new AutonomousGenerationRetryPolicy(maxAttempts: 3);
        var attempts = 0;

        await policy.ExecuteAsync((_, _) =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.CompletedTask;
        });

        Assert.Equal(3, attempts);
    }

    private static AutonomousGenerationOrchestrator CreateOrchestrator(
        FakeAutonomousRunService runService,
        FakeStoryGenerationService storyService,
        FakeImageGenerationService imageService,
        FakeVideoGenerationService videoService,
        AutonomousGenerationOptions? options = null) =>
        new(
            runService,
            storyService,
            imageService,
            videoService,
            new FakeAudioGenerationService(),
            new FakeVideoGenerationRequestFactory(),
            new FakeFinalMovieAssemblyService(),
            new AutonomousGenerationRetryPolicy(maxAttempts: 2),
            Microsoft.Extensions.Options.Options.Create(options ?? new AutonomousGenerationOptions
            {
                HeartbeatIntervalSeconds = 1,
                LeaseExtensionSeconds = 60,
                StaleHeartbeatSeconds = 30
            }),
            NullLogger<AutonomousGenerationOrchestrator>.Instance);

    private sealed class FakeAutonomousRunService : IAutonomousGenerationRunService
    {
        private readonly TemporaryMediaFiles _files;

        private FakeAutonomousRunService(TemporaryMediaFiles files)
        {
            _files = files;
            Run = new AutonomousGenerationRun
            {
                Id = 101,
                FilmProjectId = 7,
                Status = AutonomousGenerationRunStatus.Pending,
                CurrentStage = AutonomousGenerationStage.Pending,
                StartedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                LastHeartbeatAtUtc = DateTime.UtcNow,
                ConfigurationSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new AutonomousGenerationConfigurationSnapshot
                {
                    FilmProjectId = 7,
                    ProjectName = "Auto",
                    CalculatedClipCount = 1,
                    ClipDurationSeconds = 5,
                    Resolution = "1024x1024",
                    ImageModelType = "qwen_image_20B",
                    VideoModelType = "ltx2_22B_distilled_gguf_q4_k_m",
                    GenerateAudio = false,
                    PreferLtxNativeDialogue = false
                })
            };
            Project = new FilmProject
            {
                Id = 7,
                ProjectName = "Auto",
                Subject = "subject",
                CalculatedClipCount = 1,
                ClipDurationSeconds = 5,
                Resolution = "1024x1024"
            };
            Scenes.Add(new FilmScene
            {
                Id = 11,
                FilmProjectId = 7,
                SceneNumber = 1,
                DurationSeconds = 5,
                ImagePrompt = "image",
                VideoPrompt = "video",
                DialogueJson = "[]"
            });
            WorkItems.Add(new AutonomousSceneWorkItem
            {
                Id = 301,
                AutonomousGenerationRunId = 101,
                StorySceneId = 11,
                SceneNumber = 1
            });
        }

        public AutonomousGenerationRun Run { get; }
        public FilmProject Project { get; }
        public FilmScene Scene => Scenes.Single();
        public AutonomousSceneWorkItem WorkItem => WorkItems.Single();
        public List<FilmScene> Scenes { get; } = [];
        public List<AutonomousSceneWorkItem> WorkItems { get; } = [];
        public SceneMediaAsset? SelectedImageAsset { get; set; }
        public SceneMediaAsset? SelectedVideoAsset { get; set; }

        public static FakeAutonomousRunService Create(TemporaryMediaFiles files) => new(files);

        public Task<AutonomousGenerationRunSummary> StartOrGetActiveRunAsync(int filmProjectId, AutonomousGenerationConfigurationSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AutonomousGenerationRun?> GetRunAsync(int runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AutonomousGenerationRun?>(Run);

        public Task<AutonomousGenerationRunSummary?> GetLatestRunForProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AutonomousGenerationRunSummary?>(null);

        public Task<IReadOnlyList<AutonomousGenerationRunSummary>> GetRunnableRunsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AutonomousGenerationRunSummary>>(Array.Empty<AutonomousGenerationRunSummary>());

        public Task<bool> TryClaimRunAsync(int runId, string workerId, TimeSpan staleHeartbeatThreshold, TimeSpan leaseExtension, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            if (Run.WorkerId is not null &&
                !string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal) &&
                Run.LastHeartbeatAtUtc >= now - staleHeartbeatThreshold)
            {
                return Task.FromResult(false);
            }

            Run.WorkerId = workerId;
            Run.LeaseExpiresAtUtc = now + leaseExtension;
            Run.LastHeartbeatAtUtc = now;
            return Task.FromResult(true);
        }

        public Task<bool> TryRenewLeaseAsync(int runId, string workerId, TimeSpan leaseExtension, string message, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Run.LeaseExpiresAtUtc = DateTime.UtcNow + leaseExtension;
            Run.LastHeartbeatAtUtc = DateTime.UtcNow;
            Run.LastMessage = message;
            return Task.FromResult(true);
        }

        public Task<bool> IsRunOwnedByWorkerAsync(int runId, string workerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(Run.WorkerId) || string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal));

        public Task ReleaseClaimAsync(int runId, string workerId, CancellationToken cancellationToken = default)
        {
            if (string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal))
            {
                Run.WorkerId = null;
                Run.LeaseExpiresAtUtc = null;
            }

            return Task.CompletedTask;
        }

        public Task<FilmProject> GetProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project);

        public Task<IReadOnlyList<FilmScene>> GetScenesAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FilmScene>>(Scenes.ToList());

        public Task<IReadOnlyList<AutonomousSceneWorkItem>> EnsureSceneWorkItemsAsync(int runId, CancellationToken cancellationToken = default)
        {
            foreach (var scene in Scenes.Where(scene => WorkItems.All(item => item.StorySceneId != scene.Id)))
            {
                WorkItems.Add(new AutonomousSceneWorkItem
                {
                    Id = 301 + WorkItems.Count,
                    AutonomousGenerationRunId = runId,
                    StorySceneId = scene.Id,
                    SceneNumber = scene.SceneNumber,
                    ImageStatus = AutonomousWorkItemStatus.Pending,
                    VideoStatus = AutonomousWorkItemStatus.Pending,
                    AudioStatus = AutonomousWorkItemStatus.Pending,
                    FinalizationStatus = AutonomousWorkItemStatus.Pending
                });
            }

            Run.TotalSceneCount = Scenes.Count;
            return Task.FromResult<IReadOnlyList<AutonomousSceneWorkItem>>(WorkItems.OrderBy(item => item.SceneNumber).ToList());
        }

        public Task<SceneMediaAsset?> FindValidSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SelectedImageAsset);

        public Task<SceneMediaAsset?> FindValidSelectedVideoAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(SelectedVideoAsset);

        public Task<SceneMediaAsset?> FindValidSceneAudioAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SceneMediaAsset?>(null);

        public Task<IReadOnlyList<SceneSpeechSegment>> GetSpeechSegmentsAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SceneSpeechSegment>>(Array.Empty<SceneSpeechSegment>());

        public Task MarkHeartbeatAsync(int runId, string message, double? overallProgressPercentage = null, CancellationToken cancellationToken = default)
        {
            Run.LastMessage = message;
            if (overallProgressPercentage is not null)
            {
                Run.OverallProgressPercentage = overallProgressPercentage.Value;
            }

            return Task.CompletedTask;
        }

        public Task TransitionAsync(int runId, AutonomousGenerationRunStatus status, string message, CancellationToken cancellationToken = default)
        {
            var stateMachine = new AutonomousGenerationStateMachine();
            if (!stateMachine.CanTransition(Run.Status, status))
            {
                throw new InvalidOperationException($"Invalid transition: {Run.Status} -> {status}");
            }

            Run.Status = status;
            Run.CurrentStage = status switch
            {
                AutonomousGenerationRunStatus.Completed => AutonomousGenerationStage.Completed,
                AutonomousGenerationRunStatus.Cancelled => AutonomousGenerationStage.Cancelled,
                _ => (AutonomousGenerationStage)(int)status
            };
            Run.LastMessage = message;
            return Task.CompletedTask;
        }

        public Task SetCurrentSceneAsync(int runId, int? sceneId, int? sceneNumber, CancellationToken cancellationToken = default)
        {
            Run.CurrentSceneId = sceneId;
            Run.CurrentSceneNumber = sceneNumber;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemImageAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default)
        {
            WorkItem.ImageStatus = status;
            WorkItem.ImageMediaAssetId = mediaAssetId;
            if (incrementAttempt) WorkItem.ImageAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemVideoAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default)
        {
            WorkItem.VideoStatus = status;
            WorkItem.VideoMediaAssetId = mediaAssetId;
            if (incrementAttempt) WorkItem.VideoAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemAudioAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default)
        {
            WorkItem.AudioStatus = status;
            WorkItem.AudioMediaAssetId = mediaAssetId;
            if (incrementAttempt) WorkItem.AudioAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemFinalizationAsync(int workItemId, AutonomousWorkItemStatus status, string? error, CancellationToken cancellationToken = default)
        {
            WorkItem.FinalizationStatus = status;
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(int runId, string message, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Completed;
            Run.CurrentStage = AutonomousGenerationStage.Completed;
            Run.LastMessage = message;
            Run.OverallProgressPercentage = 100;
            return Task.CompletedTask;
        }

        public Task FailRunAsync(int runId, string error, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Failed;
            Run.LastError = error;
            return Task.CompletedTask;
        }

        public Task RequestCancellationAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.CancellationRequested = true;
            Run.Status = AutonomousGenerationRunStatus.CancelRequested;
            return Task.CompletedTask;
        }

        public Task PauseAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Paused;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Pending;
            return Task.CompletedTask;
        }

        public Task RetryAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Pending;
            Run.CurrentStage = AutonomousGenerationStage.Pending;
            Run.LastError = string.Empty;
            Run.AttemptCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStoryGenerationService : IStoryGenerationService
    {
        public int GenerateMissingScenesCallCount { get; private set; }
        public int ExistingStoryCount { get; set; }
        public int StoryRegenerationCallCount { get; private set; }
        public Action<int>? CreateMissingScenes { get; set; }

        public Task<StoryGenerationProgressResult> GenerateStoryAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            StoryRegenerationCallCount++;
            return GenerateAllMissingScenesAsync(filmProjectId, progress, cancellationToken);
        }

        public Task<StoryGenerationProgressResult> GenerateAllMissingScenesAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateMissingScenesCallCount++;
            CreateMissingScenes?.Invoke(filmProjectId);
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 1 });
        }

        public Task<StoryGenerationProgressResult> GenerateNextMissingSceneAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateAllMissingScenesAsync(filmProjectId, progress, cancellationToken);

        public Task<StoryGenerationProgressResult> GenerateUpToMissingScenesAsync(int filmProjectId, int maximumSceneCount, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateAllMissingScenesAsync(filmProjectId, progress, cancellationToken);
    }

    private sealed class FakeImageGenerationService : IImageGenerationService
    {
        private readonly FakeAutonomousRunService _runService;
        private readonly TemporaryMediaFiles _files;

        public FakeImageGenerationService(FakeAutonomousRunService runService, TemporaryMediaFiles files)
        {
            _runService = runService;
            _files = files;
        }

        public int GenerateCallCount { get; private set; }
        public WanGpImageGenerationRequest? LastRequest { get; private set; }
        public TimeSpan DelayBeforeCompleting { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GenerationJob> GenerateSceneImageAsync(int sceneId, WanGpImageGenerationRequest request, IProgress<MediaGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateCallCount++;
            LastRequest = request;
            Started.TrySetResult();
            if (DelayBeforeCompleting > TimeSpan.Zero)
            {
                await Task.Delay(DelayBeforeCompleting, cancellationToken);
            }

            _runService.SelectedImageAsset = _files.CreateAsset(401, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
            return new GenerationJob { Id = 21, SceneId = sceneId, Status = GenerationJobStatus.Completed };
        }

        public Task GenerateMissingImagesAsync(int filmProjectId, WanGpImageGenerationRequest templateRequest, bool stopOnError, IProgress<MediaGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelActiveJobAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSelectedAssetAsync(int assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SceneMediaAsset> ImportExistingWanGpOutputAsync(int sceneId, string sourcePath, bool makeSelected = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkOrphanRunningJobsInterruptedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeVideoGenerationService : IVideoGenerationService
    {
        private readonly FakeAutonomousRunService _runService;
        private readonly TemporaryMediaFiles _files;

        public FakeVideoGenerationService(FakeAutonomousRunService runService, TemporaryMediaFiles files)
        {
            _runService = runService;
            _files = files;
        }

        public int GenerateCallCount { get; private set; }
        public WanGpVideoGenerationRequest? LastRequest { get; private set; }

        public Task<GenerationJob> GenerateSceneVideoAsync(WanGpVideoGenerationRequest request, IProgress<MediaGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateCallCount++;
            LastRequest = request;
            _runService.SelectedVideoAsset = _files.CreateAsset(501, MediaType.Video, MediaAssetRole.GeneratedSilentVideo, selected: true);
            return Task.FromResult(new GenerationJob { Id = 31, SceneId = request.SceneId, Status = GenerationJobStatus.Completed });
        }

        public Task CancelActiveJobAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSelectedVideoAssetAsync(int assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeVideoGenerationRequestFactory : IVideoGenerationRequestFactory
    {
        public Task<WanGpVideoGenerationRequest> CreateAsync(VideoGenerationRequestFactoryInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WanGpVideoGenerationRequest
            {
                FilmProjectId = input.FilmProjectId,
                SceneId = input.Scene.Id,
                SceneNumber = input.Scene.SceneNumber,
                SourceImageAssetId = input.SourceImageAsset.Id,
                SourceImagePath = input.SourceImageAsset.FilePath,
                ModelType = input.ModelType,
                Prompt = input.Scene.VideoPrompt,
                Resolution = input.Resolution,
                DurationSeconds = input.Scene.DurationSeconds,
                InferenceSteps = input.InferenceSteps,
                StopOnFailure = true
            });
    }

    private sealed class FakeAudioGenerationService : IAudioGenerationService
    {
        public Task<AudioModelDiscoveryResult> DiscoverKugelAudioAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioModelDiscoveryResult());

        public Task<SceneSpeechPlan> CreateBasicSpeechPlanAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneSpeechPlan { Id = 1, SceneId = sceneId });

        public Task<SceneMediaAsset> GenerateSpeechSegmentAsync(int speechSegmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneMediaAsset { Id = speechSegmentId, MediaType = MediaType.Audio });

        public Task<SceneMediaAsset> CreateSpeechTrackForSceneAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneMediaAsset { Id = 701, SceneId = sceneId, MediaType = MediaType.Audio });

        public Task<SceneMediaAsset> CreateFinalDialogueVideoForSceneAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneMediaAsset { Id = 801, SceneId = sceneId, MediaType = MediaType.Video });
    }

    private sealed class FakeFinalMovieAssemblyService : IFinalMovieAssemblyService
    {
        public Task<string> AssembleLtxNativeDialogueMovieAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class TemporaryMediaFiles : IDisposable
    {
        private readonly List<string> _paths = [];

        public SceneMediaAsset CreateAsset(int id, MediaType mediaType, MediaAssetRole role, bool selected)
        {
            var path = Path.Combine(Path.GetTempPath(), $"director-auto-test-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            _paths.Add(path);
            return new SceneMediaAsset
            {
                Id = id,
                FilmProjectId = 7,
                SceneId = 11,
                MediaType = mediaType,
                Role = role,
                FilePath = path,
                FileSize = 4,
                IsSelected = selected,
                CreatedAt = DateTime.Now
            };
        }

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
