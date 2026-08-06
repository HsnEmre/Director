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

    private sealed class RecordingFilmProjectService : IFilmProjectService
    {
        public FilmProject? CreatedProject { get; private set; }

        public Task<List<FilmProject>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<FilmProject>());

        public Task<FilmProject?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult<FilmProject?>(CreatedProject?.Id == id ? CreatedProject : null);

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

        public Task<AutonomousGenerationRunSummary> StartOrGetActiveRunAsync(
            int filmProjectId,
            AutonomousGenerationConfigurationSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            CapturedSnapshot = snapshot;
            Started.TrySetResult();
            return Task.FromResult(new AutonomousGenerationRunSummary
            {
                Id = 88,
                FilmProjectId = filmProjectId,
                Status = AutonomousGenerationRunStatus.Pending,
                CurrentStage = AutonomousGenerationStage.Pending
            });
        }

        public Task<AutonomousGenerationRun?> GetRunAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AutonomousGenerationRunSummary?> GetLatestRunForProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) => Task.FromResult<AutonomousGenerationRunSummary?>(null);
        public Task<IReadOnlyList<AutonomousGenerationRunSummary>> GetRunnableRunsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryClaimRunAsync(int runId, string workerId, TimeSpan staleHeartbeatThreshold, TimeSpan leaseExtension, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> TryRenewLeaseAsync(int runId, string workerId, TimeSpan leaseExtension, string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsRunOwnedByWorkerAsync(int runId, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReleaseClaimAsync(int runId, string workerId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FilmProject> GetProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FilmScene>> GetScenesAsync(int filmProjectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AutonomousSceneWorkItem>> EnsureSceneWorkItemsAsync(int runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidSelectedVideoAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SceneMediaAsset?> FindValidSceneAudioAssetAsync(int sceneId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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
        public Task NavigateToProjectSetupAsync(int? projectId = null) => Task.CompletedTask;
        public Task NavigateToStoryGenerationAsync(int projectId) => Task.CompletedTask;
        public Task NavigateToProjectHistoryAsync() => Task.CompletedTask;
        public Task NavigateToProductionAsync(int projectId) => Task.CompletedTask;
    }
}
