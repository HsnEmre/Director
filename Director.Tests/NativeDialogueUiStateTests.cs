using System.Collections.ObjectModel;
using Director.Enums;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.ViewModels;
using Director.WanGp;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class NativeDialogueUiStateTests
{
    [Fact]
    public async Task SuccessfulNativeDialogueGeneration_ReleasesUiState()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"director-ui-success-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        try
        {
            var activity = new RecordingActivityCenter();
            var videoService = new RecordingVideoGenerationService();
            var viewModel = CreateViewModel(new SuccessfulComposer(), videoService, activity, imagePath);

            Assert.True(viewModel.GenerateVideoCommand.CanExecute(null));
            viewModel.GenerateVideoCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && videoService.CallCount == 0; attempt++) await Task.Delay(10);
            for (var attempt = 0; attempt < 100 && viewModel.IsBusy; attempt++) await Task.Delay(10);

            Assert.Equal(1, videoService.CallCount);
            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.CancelCommand.CanExecute(null));
            Assert.False(activity.Snapshot.HasActiveOperation);
            Assert.Equal(GenerationJobStatus.Completed, activity.Snapshot.OperationStatus);
        }
        finally
        {
            try { File.Delete(imagePath); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task TypedNativeDialogueFailure_ReleasesUiStateAndAllowsRetry()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"director-ui-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        try
        {
            var activity = new RecordingActivityCenter();
            var composer = new FailingComposer();
            var videoService = new RecordingVideoGenerationService();
            var viewModel = CreateViewModel(composer, videoService, activity, imagePath);

            Assert.True(viewModel.GenerateVideoCommand.CanExecute(null));
            viewModel.GenerateVideoCommand.Execute(null);
            await composer.Called.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (var attempt = 0; attempt < 100 && viewModel.IsBusy; attempt++) await Task.Delay(10);

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.CancelCommand.CanExecute(null));
            Assert.True(viewModel.GenerateVideoCommand.CanExecute(null));
            Assert.False(activity.Snapshot.HasActiveOperation);
            Assert.Equal(GenerationJobStatus.Failed, activity.Snapshot.OperationStatus);
            Assert.Contains("Video üretimi başlatılmadı", viewModel.VideoStatus, StringComparison.Ordinal);
            Assert.Contains("ResponseValidation", activity.Logs.Last().Message, StringComparison.Ordinal);
            Assert.Equal(0, videoService.CallCount);
        }
        finally
        {
            try
            {
                File.Delete(imagePath);
            }
            catch (IOException)
            {
                // WPF's preview decoder may retain the test bitmap until dispatcher cleanup.
            }
        }
    }

    private static ProductionWorkspaceViewModel CreateViewModel(
        ILtxNativeDialoguePromptComposer composer,
        RecordingVideoGenerationService videoService,
        RecordingActivityCenter activity,
        string imagePath)
    {
        var viewModel = new ProductionWorkspaceViewModel(
            null!, null!, null!, null!, null!, null!, null!, null!, composer, null!, videoService, null!,
            new AudioProductionViewModel(null!), new IdleGpuCoordinator(), activity,
            Microsoft.Extensions.Options.Options.Create(new WanGpOptions()),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()));
        viewModel.SelectedScene = new ProductionSceneRowViewModel
        {
            Id = 36,
            SceneNumber = 1,
            VideoPrompt = "camera moves slowly",
            DialogueJson = """[{"speakerKey":"kara_vezir","text":"Dur."}]""",
            Assets = [new SceneMediaAssetRowViewModel { Id = 31, FilePath = imagePath, IsSelected = true }]
        };
        viewModel.SelectedVideoModel = new WanGpVideoModelOptionViewModel
        {
            ModelType = "ltxv_13b_0.9.8_distilled",
            InstallationStatus = WanGpModelInstallStatus.Installed,
            SupportsImageToVideo = true,
            SupportsStartImage = true,
            SupportsAudioOutput = true,
            InputContractValidated = true,
            ResolvedStartImageKey = "image_start",
            NativeDialogueSupported = true,
            InputContract = new WanGpVideoInputContract
            {
                IsValidated = true,
                SupportsImageToVideo = true,
                SupportsStartImage = true,
                StartImageKey = "image_start"
            }
        };
        return viewModel;
    }

    private sealed class FailingComposer : ILtxNativeDialoguePromptComposer
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<LtxNativeDialoguePromptResult> BuildAsync(int sceneId, int referenceImageAssetId, CancellationToken cancellationToken = default)
        {
            Called.TrySetResult();
            return Task.FromException<LtxNativeDialoguePromptResult>(new NativeDialoguePromptCompositionException(
                9, sceneId, 1, NativeDialoguePromptFailureStage.ResponseValidation,
                "combinedPrompt exact konuşma satırını içermiyor", "C:\\diagnostics\\failure.json"));
        }

        public Task<LtxNativeDialoguePromptResult> BuildReadOnlyAsync(int sceneId, int referenceImageAssetId,
            bool allowRepair = false, CancellationToken cancellationToken = default) =>
            BuildAsync(sceneId, referenceImageAssetId, cancellationToken);
    }

    private sealed class SuccessfulComposer : ILtxNativeDialoguePromptComposer
    {
        public Task<LtxNativeDialoguePromptResult> BuildAsync(int sceneId, int referenceImageAssetId,
            CancellationToken cancellationToken = default) => Task.FromResult(new LtxNativeDialoguePromptResult
        {
            FilmProjectId = 9,
            SceneId = sceneId,
            SceneNumber = 1,
            HasDialogue = true,
            IsValid = true,
            DialogueCount = 1,
            SpeakerCount = 1,
            SpeakerKey = "kara_vezir",
            SpeakerDisplayName = "Kara Vezir",
            ExactDialogue = "Dur.",
            ExactSpokenLines = ["Dur."],
            DialogueSourceHash = new string('a', 64),
            VideoPrompt = "camera moves slowly",
            VoiceDirection = "valid voice direction",
            CombinedPrompt = "Kara Vezir says in Turkish: \"Dur.\"\nOnly Kara Vezir speaks"
        });

        public Task<LtxNativeDialoguePromptResult> BuildReadOnlyAsync(int sceneId, int referenceImageAssetId,
            bool allowRepair = false, CancellationToken cancellationToken = default) =>
            BuildAsync(sceneId, referenceImageAssetId, cancellationToken);
    }

    private sealed class IdleGpuCoordinator : IGpuGenerationCoordinator
    {
        public bool IsBusy => false;
        public Task<IAsyncDisposable> AcquireAsync(GenerationOperationType operationType, int projectId, int sceneId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingVideoGenerationService : IVideoGenerationService
    {
        public int CallCount { get; private set; }
        public Task<Director.Models.GenerationJob> GenerateSceneVideoAsync(WanGpVideoGenerationRequest request,
            IProgress<Director.Dtos.MediaGeneration.MediaGenerationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new Director.Models.GenerationJob
            {
                Id = 1,
                SceneId = request.SceneId,
                FilmProjectId = request.FilmProjectId,
                Status = GenerationJobStatus.Completed
            });
        }

        public Task CancelActiveJobAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSelectedVideoAssetAsync(int assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingActivityCenter : IApplicationActivityCenter
    {
        public ApplicationActivitySnapshot Snapshot { get; } = new() { McpState = WanGpMcpConnectionState.Connected };
        public ObservableCollection<ProductionLogEntry> Logs { get; } = [];
        public event EventHandler? Changed;
        public void SetRuntimeStatus(WanGpRuntimeStatus status) { }
        public void SetActiveJob(int? jobId, string? externalJobId) { }
        public void UpdateProgress(double progress, string phase, int? currentStep = null, int? totalSteps = null) { }
        public void ClearLogs() => Logs.Clear();
        public void SetModelDiscoveryStatus(string status) { }
        public void SetError(string message) { }

        public void StartOperation(string operationName, int? projectId, string projectName, int? sceneId, int? sceneNumber)
        {
            Snapshot.HasActiveOperation = true;
            Snapshot.OperationStatus = GenerationJobStatus.Running;
        }

        public void CompleteOperation(GenerationJobStatus status, string message)
        {
            Snapshot.HasActiveOperation = false;
            Snapshot.OperationStatus = status;
            AddLog(status.ToString(), message);
        }

        public void AddLog(string phase, string message, GenerationLogLevel level = GenerationLogLevel.Information)
        {
            Logs.Add(new ProductionLogEntry { Phase = phase, Message = message, Level = level });
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
