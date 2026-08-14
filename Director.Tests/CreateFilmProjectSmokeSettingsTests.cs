using Director.Dtos;
using Director.Dtos.Autonomous;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.ViewModels;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class CreateFilmProjectSmokeSettingsTests
{
    [Fact]
    public async Task SecondBasedSmokeTarget_CreatesSingleSceneAutonomousSnapshotWithAudioAndNativeDialogueOff()
    {
        var projects = new RecordingFilmProjectService();
        var runs = new RecordingAutonomousRunService();
        var viewModel = new CreateFilmProjectViewModel(
            projects,
            runs,
            new NoopMessageService(),
            new NoopNavigationService(),
            new VideoModelCapabilityService(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions { StoryTextModel = "qwen3-vl:30b-a3b-instruct" }));

        viewModel.ProjectName = "Autonomous Smoke 10s 20260806";
        viewModel.Subject = "Rainy street with a red umbrella.";
        viewModel.UseSecondBasedTargetDuration = true;
        viewModel.TargetDurationSeconds = 10;
        viewModel.ClipDurationSeconds = 10;
        viewModel.StoryGenre = "Dram";
        viewModel.VisualStyle = "Sinematik Gercekci";
        viewModel.VideoStyle = "Yavas ve Atmosferik";
        viewModel.AspectRatio = "16:9";
        viewModel.Resolution = "1280x720";
        viewModel.UseNarrator = false;
        viewModel.PreferLtxNativeDialogue = false;
        viewModel.IsAutonomousMode = true;

        Assert.Equal(1, viewModel.CalculatedClipCount);
        Assert.Contains(10, viewModel.ClipDurationOptions);
        Assert.DoesNotContain(60, viewModel.ClipDurationOptions);

        viewModel.ContinueCommand.Execute(null);
        await runs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(projects.CreatedProject);
        Assert.Equal(1, projects.CreatedProject!.TotalDurationMinutes);
        Assert.Equal(10, projects.CreatedProject.ClipDurationSeconds);
        Assert.Equal(1, projects.CreatedProject.CalculatedClipCount);
        Assert.Equal("1280x720", projects.CreatedProject.Resolution);
        Assert.True(projects.CreatedProject.AutonomousModeEnabled);

        var snapshot = runs.CapturedSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot!.TargetDurationSeconds);
        Assert.Equal(1, snapshot.TotalDurationMinutes);
        Assert.Equal(10, snapshot.ClipDurationSeconds);
        Assert.Equal(1, snapshot.CalculatedClipCount);
        Assert.Equal("1280x720", snapshot.Resolution);
        Assert.Equal(VideoModelCapabilityService.VerifiedLtxModelType, snapshot.VideoModelType);
        Assert.False(snapshot.GenerateAudio);
        Assert.False(snapshot.PreferLtxNativeDialogue);
        Assert.Equal("qwen3-vl:30b-a3b-instruct", snapshot.StoryModel);
    }

    [Fact]
    public async Task FailedAutonomousRunResume_CreatesNewRunAndKeepsFailedHistoryUnchanged()
    {
        var projects = new RecordingFilmProjectService();
        projects.SetExistingProject(new FilmProject
        {
            Id = 13,
            ProjectName = "Failed Resume",
            Subject = "Resume from real checkpoints.",
            TotalDurationMinutes = 1,
            ClipDurationSeconds = 10,
            CalculatedClipCount = 6,
            Language = "Türkçe",
            TargetAudience = "Genel İzleyici",
            StoryGenre = "Dram",
            VisualStyle = "Sinematik Gercekci",
            VideoStyle = "Yavas ve Atmosferik",
            AspectRatio = "16:9",
            Resolution = "1280x720",
            AutonomousModeEnabled = true,
            Status = FilmProjectStatus.ProductionStarted
        });
        var failedHistory = new AutonomousGenerationRunSummary
        {
            Id = 4,
            FilmProjectId = 13,
            Status = AutonomousGenerationRunStatus.Failed,
            CurrentStage = AutonomousGenerationStage.Failed,
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            LastError = "Hedef makine etkin olarak reddettiğinden bağlantı kurulamadı. (127.0.0.1:8000)",
            LastMessage = "Otonom üretim hata ile durdu."
        };
        var runs = new RecordingAutonomousRunService
        {
            LatestSummary = failedHistory,
            NewRunId = 99
        };
        var viewModel = new CreateFilmProjectViewModel(
            projects,
            runs,
            new NoopMessageService(),
            new NoopNavigationService(),
            new VideoModelCapabilityService(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions { StoryTextModel = "qwen3-vl:30b-a3b-instruct" }));

        await viewModel.LoadProjectAsync(13);

        viewModel.ResumeAutonomousCommand.Execute(null);
        await runs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, runs.StartCallCount);
        Assert.Equal(13, runs.StartedFilmProjectId);
        Assert.NotNull(runs.CapturedSnapshot);
        Assert.Equal(6, runs.CapturedSnapshot!.CalculatedClipCount);
        Assert.Equal(4, failedHistory.Id);
        Assert.Equal(AutonomousGenerationRunStatus.Failed, failedHistory.Status);
        Assert.NotNull(failedHistory.CompletedAtUtc);
        Assert.Contains("127.0.0.1:8000", failedHistory.LastError);
        Assert.Equal(99, viewModel.AutonomousRunId);
        Assert.DoesNotContain("127.0.0.1:8000", viewModel.AutonomousStatusText);
    }

    [Fact]
    public async Task ActiveAutonomousRunResume_ReturnsExistingRunWithoutCreatingDuplicate()
    {
        var projects = new RecordingFilmProjectService();
        projects.SetExistingProject(new FilmProject
        {
            Id = 14,
            ProjectName = "Active Resume",
            Subject = "Do not duplicate active run.",
            TotalDurationMinutes = 1,
            ClipDurationSeconds = 10,
            CalculatedClipCount = 1,
            Language = "Türkçe",
            TargetAudience = "Genel İzleyici",
            StoryGenre = "Dram",
            VisualStyle = "Sinematik",
            VideoStyle = "Atmosferik",
            AspectRatio = "16:9",
            Resolution = "1280x720",
            AutonomousModeEnabled = true,
            Status = FilmProjectStatus.ProductionStarted
        });
        var activeSummary = new AutonomousGenerationRunSummary
        {
            Id = 41,
            FilmProjectId = 14,
            Status = AutonomousGenerationRunStatus.GeneratingVideoPrompts,
            CurrentStage = AutonomousGenerationStage.GeneratingVideoPrompts
        };
        var runs = new RecordingAutonomousRunService
        {
            LatestSummary = activeSummary,
            ReturnActiveSummary = activeSummary
        };
        var viewModel = new CreateFilmProjectViewModel(
            projects,
            runs,
            new NoopMessageService(),
            new NoopNavigationService(),
            new VideoModelCapabilityService(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));

        await viewModel.LoadProjectAsync(14);

        Assert.False(viewModel.ContinueCommand.CanExecute(null));
        viewModel.ResumeAutonomousCommand.Execute(null);
        await runs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, runs.StartCallCount);
        Assert.Equal(41, viewModel.AutonomousRunId);
    }

    [Fact]
    public async Task AutonomousContinue_WithImageCheckpoint_NavigatesToProductionImageTab()
    {
        var projects = new RecordingFilmProjectService();
        projects.SetExistingProject(new FilmProject
        {
            Id = 18,
            ProjectName = "Image Checkpoint",
            Subject = "Text checkpoints are complete.",
            TotalDurationMinutes = 1,
            ClipDurationSeconds = 10,
            CalculatedClipCount = 6,
            Language = "Türkçe",
            TargetAudience = "Genel İzleyici",
            StoryGenre = "Fantastik",
            VisualStyle = "Sinematik",
            VideoStyle = "Sessiz sinematik",
            AspectRatio = "16:9",
            Resolution = "1280x720",
            AutonomousModeEnabled = true,
            Status = FilmProjectStatus.ProductionStarted
        });
        var runs = new RecordingAutonomousRunService
        {
            LatestSummary = new AutonomousGenerationRunSummary
            {
                Id = 8,
                FilmProjectId = 18,
                Status = AutonomousGenerationRunStatus.Failed,
                CurrentStage = AutonomousGenerationStage.Failed,
                LastError = "Hedef makine etkin olarak reddettiğinden bağlantı kurulamadı. (127.0.0.1:7866)"
            },
            Checkpoint = new AutonomousProjectCheckpoint
            {
                FilmProjectId = 18,
                ExpectedSceneCount = 6,
                SceneCount = 6,
                HasValidStory = true,
                HasValidCharacters = true,
                FirstMissingSelectedImageSceneNumber = 1
            }
        };
        var navigation = new NoopNavigationService();
        var viewModel = new CreateFilmProjectViewModel(
            projects,
            runs,
            new NoopMessageService(),
            navigation,
            new VideoModelCapabilityService(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));

        await viewModel.LoadProjectAsync(18);

        viewModel.ResumeAutonomousCommand.Execute(null);
        await runs.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(18, navigation.ProductionProjectId);
        Assert.Equal(0, navigation.ProductionTabIndex);
        Assert.Equal(0, navigation.StoryProjectId);
    }

    private sealed class RecordingFilmProjectService : IFilmProjectService
    {
        public FilmProject? CreatedProject { get; private set; }
        public FilmProject? ExistingProject { get; private set; }

        public void SetExistingProject(FilmProject project) => ExistingProject = project;

        public Task<List<FilmProject>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<FilmProject>());

        public Task<FilmProject?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult<FilmProject?>(CreatedProject?.Id == id ? CreatedProject : ExistingProject?.Id == id ? ExistingProject : null);

        public Task<FilmProject> CreateAsync(FilmProject project, CancellationToken cancellationToken = default)
        {
            project.Id = 77;
            CreatedProject = project;
            return Task.FromResult(project);
        }

        public Task UpdateAsync(FilmProject project, CancellationToken cancellationToken = default)
        {
            CreatedProject = project;
            return Task.CompletedTask;
        }

        public Task<List<FilmProjectListItemDto>> GetProjectHistoryAsync(
            string? searchText = null,
            FilmProjectStatus? status = null,
            string? storyGenre = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<FilmProjectListItemDto>());

        public Task DeleteAsync(int projectId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingAutonomousRunService : IAutonomousGenerationRunService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AutonomousGenerationConfigurationSnapshot? CapturedSnapshot { get; private set; }
        public AutonomousGenerationRunSummary? LatestSummary { get; set; }
        public AutonomousGenerationRunSummary? ReturnActiveSummary { get; set; }
        public AutonomousProjectCheckpoint Checkpoint { get; set; } = new()
        {
            HasValidStory = false
        };
        public int NewRunId { get; set; } = 88;
        public int StartCallCount { get; private set; }
        public int StartedFilmProjectId { get; private set; }

        public Task<AutonomousGenerationRunSummary> StartOrGetActiveRunAsync(
            int filmProjectId,
            AutonomousGenerationConfigurationSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            StartedFilmProjectId = filmProjectId;
            CapturedSnapshot = snapshot;
            Started.TrySetResult();
            return Task.FromResult(ReturnActiveSummary ?? new AutonomousGenerationRunSummary
            {
                Id = NewRunId,
                FilmProjectId = filmProjectId,
                Status = AutonomousGenerationRunStatus.Pending,
                CurrentStage = AutonomousGenerationStage.Pending
            });
        }

        public Task<AutonomousGenerationRun?> GetRunAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutonomousGenerationRunSummary?> GetLatestRunForProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) => Task.FromResult(LatestSummary);
        public Task<AutonomousProjectCheckpoint> GetProjectCheckpointAsync(int filmProjectId, CancellationToken cancellationToken = default) => Task.FromResult(Checkpoint);
        public Task<IReadOnlyList<AutonomousGenerationRunSummary>> GetRunnableRunsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryClaimRunAsync(int runId, string workerId, TimeSpan staleHeartbeatThreshold, TimeSpan leaseExtension, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRenewLeaseAsync(int runId, string workerId, TimeSpan leaseExtension, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsRunOwnedByWorkerAsync(int runId, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseClaimAsync(int runId, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FilmProject> GetProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FilmScene>> GetScenesAsync(int filmProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutonomousSceneWorkItem>> EnsureSceneWorkItemsAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidSelectedVideoAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidSceneAudioAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> HasActiveGenerationJobAsync(int sceneId, MediaType mediaType, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SceneSpeechSegment>> GetSpeechSegmentsAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkHeartbeatAsync(int runId, string message, double? overallProgressPercentage = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task TransitionAsync(int runId, AutonomousGenerationRunStatus status, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetCurrentSceneAsync(int runId, int? sceneId, int? sceneNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkWorkItemImageAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkWorkItemVideoAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkWorkItemAudioAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkWorkItemFinalizationAsync(int workItemId, AutonomousWorkItemStatus status, string? error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CompleteRunAsync(int runId, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task FailRunAsync(int runId, string error, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RequestCancellationAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task PauseAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RetryAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopMessageService : IMessageService
    {
        public void ShowInfo(string message, string title = "Director") { }
        public void ShowError(string message, string title = "Director") { }
        public bool Confirm(string message, string title = "Director") => true;
    }

    private sealed class NoopNavigationService : INavigationService
    {
        public object? CurrentViewModel => null;
        public string CurrentStep => "Project Setup";
        public int? CurrentProjectId => null;
        public int StoryProjectId { get; private set; }
        public int ProductionProjectId { get; private set; }
        public int ProductionTabIndex { get; private set; } = -1;
        public Task NavigateToProjectSetupAsync(int? projectId = null) => Task.CompletedTask;
        public Task NavigateToStoryGenerationAsync(int projectId)
        {
            StoryProjectId = projectId;
            return Task.CompletedTask;
        }
        public Task NavigateToProjectHistoryAsync() => Task.CompletedTask;
        public Task NavigateToProductionAsync(int projectId, int selectedTabIndex = 0)
        {
            ProductionProjectId = projectId;
            ProductionTabIndex = selectedTabIndex;
            return Task.CompletedTask;
        }
    }
}
