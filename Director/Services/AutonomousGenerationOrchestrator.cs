using System.Text.Json;
using Director.Dtos.Autonomous;
using Director.Dtos.MediaGeneration;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class AutonomousGenerationOrchestrator : IAutonomousGenerationOrchestrator
{
    private readonly IAutonomousGenerationRunService _runService;
    private readonly IStoryGenerationService _storyGenerationService;
    private readonly IImageGenerationService _imageGenerationService;
    private readonly IVideoGenerationService _videoGenerationService;
    private readonly IAudioGenerationService _audioGenerationService;
    private readonly IVideoGenerationRequestFactory _videoGenerationRequestFactory;
    private readonly IFinalMovieAssemblyService _finalMovieAssemblyService;
    private readonly AutonomousGenerationRetryPolicy _retryPolicy;
    private readonly AutonomousGenerationOptions _options;
    private readonly ILogger<AutonomousGenerationOrchestrator> _logger;

    public AutonomousGenerationOrchestrator(
        IAutonomousGenerationRunService runService,
        IStoryGenerationService storyGenerationService,
        IImageGenerationService imageGenerationService,
        IVideoGenerationService videoGenerationService,
        IAudioGenerationService audioGenerationService,
        IVideoGenerationRequestFactory videoGenerationRequestFactory,
        IFinalMovieAssemblyService finalMovieAssemblyService,
        AutonomousGenerationRetryPolicy retryPolicy,
        IOptions<AutonomousGenerationOptions> options,
        ILogger<AutonomousGenerationOrchestrator> logger)
    {
        _runService = runService;
        _storyGenerationService = storyGenerationService;
        _imageGenerationService = imageGenerationService;
        _videoGenerationService = videoGenerationService;
        _audioGenerationService = audioGenerationService;
        _videoGenerationRequestFactory = videoGenerationRequestFactory;
        _finalMovieAssemblyService = finalMovieAssemblyService;
        _retryPolicy = retryPolicy;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RunAsync(int runId, string? workerId = null, CancellationToken cancellationToken = default)
    {
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = string.IsNullOrWhiteSpace(workerId)
            ? Task.CompletedTask
            : RunLeaseRenewalLoopAsync(runId, workerId, renewalCancellation.Token);

        try
        {
            if (!await EnsureCanContinueAsync(runId, workerId, cancellationToken))
            {
                return;
            }

            var run = await RequireRunAsync(runId, cancellationToken);
            var resumeStatus = run.Status;
            var project = await _runService.GetProjectAsync(run.FilmProjectId, cancellationToken);
            var snapshot = DeserializeSnapshot(run, project);

            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.Validating, "Otonom üretim doğrulanıyor.", cancellationToken);
            await ValidateProjectAsync(runId, project, snapshot, cancellationToken);

            if (!await EnsureCanContinueAsync(runId, workerId, cancellationToken))
            {
                return;
            }

            if (ShouldRunStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingScenes))
            {
                await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingStory, "Hikaye ve eksik sahneler üretiliyor.", cancellationToken);
                await _storyGenerationService.GenerateAllMissingScenesAsync(
                    project.Id,
                    new Progress<StoryGenerationProgress>(progress =>
                        _ = _runService.MarkHeartbeatAsync(runId, progress.Message, Math.Min(20, progress.Percentage * 0.2), CancellationToken.None)),
                    cancellationToken);
            }

            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingScenes, "Sahne checkpoint kayıtları hazırlanıyor.", cancellationToken);
            var workItems = await _runService.EnsureSceneWorkItemsAsync(runId, cancellationToken);
            if (workItems.Count == 0)
            {
                throw new InvalidOperationException("Otonom üretim için sahne bulunamadı.");
            }

            if (ShouldRunStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingImages))
            {
                await GenerateImagesAsync(runId, workerId, snapshot, workItems, cancellationToken);
            }

            if (ShouldRunStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingVideos))
            {
                await GenerateVideosAsync(runId, workerId, snapshot, cancellationToken);
            }

            if (ShouldRunStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingAudio))
            {
                await GenerateAudioAsync(runId, workerId, snapshot, cancellationToken);
            }

            await FinalizeAsync(runId, workerId, snapshot, cancellationToken);
            await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
            await _runService.CompleteRunAsync(runId, "Otonom üretim tamamlandı.", cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await TryCancelRunAsync(runId, "Otonom üretim iptal edildi.", CancellationToken.None);
        }
        catch (WorkerOwnershipLostException ex)
        {
            _logger.LogWarning(ex, "Autonomous generation run {RunId} stopped because worker ownership was lost.", runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autonomous generation run {RunId} failed.", runId);
            await _runService.FailRunAsync(runId, ex.Message, CancellationToken.None);
        }
        finally
        {
            await StopLeaseRenewalAsync(runId, workerId, renewalCancellation, renewalTask);
        }
    }

    private async Task GenerateImagesAsync(
        int runId,
        string? workerId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        IReadOnlyList<AutonomousSceneWorkItem> workItems,
        CancellationToken cancellationToken)
    {
        await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingImages, "Sahne görselleri üretiliyor.", cancellationToken);
        var scenes = (await _runService.GetScenesAsync(snapshot.FilmProjectId, cancellationToken)).ToDictionary(scene => scene.Id);
        for (var index = 0; index < workItems.Count; index++)
        {
            var workItem = workItems[index];
            if (!await EnsureCanContinueAsync(runId, workerId, cancellationToken))
            {
                return;
            }

            var scene = scenes[workItem.StorySceneId];
            await _runService.SetCurrentSceneAsync(runId, scene.Id, scene.SceneNumber, cancellationToken);
            var existingAsset = await _runService.FindValidSelectedImageAssetAsync(scene.Id, cancellationToken);
            if (existingAsset is not null)
            {
                await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Skipped, existingAsset.Id, null, false, cancellationToken);
                await UpdatePipelineProgressAsync(runId, "Geçerli görsel bulundu; yeniden üretilmedi.", 20, 45, index + 1, workItems.Count, cancellationToken);
                continue;
            }

            await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, null, true, cancellationToken);
            await _retryPolicy.ExecuteAsync(
                async (_, token) =>
                {
                    var request = new WanGpImageGenerationRequest
                    {
                        ModelType = string.IsNullOrWhiteSpace(snapshot.ImageModelType) ? "qwen_image_20B" : snapshot.ImageModelType,
                        Prompt = scene.ImagePrompt,
                        NegativePrompt = scene.ImageNegativePrompt,
                        Resolution = snapshot.Resolution,
                        InferenceSteps = snapshot.ImageInferenceSteps,
                        Seed = snapshot.Seed,
                        RandomSeed = snapshot.RandomSeed,
                        StopOnError = true
                    };

                    await _imageGenerationService.GenerateSceneImageAsync(scene.Id, request, BuildMediaProgress(runId, 20, 45), token);
                    await EnsureWorkerOwnershipAsync(runId, workerId, token);
                    var generatedAsset = await _runService.FindValidSelectedImageAssetAsync(scene.Id, token)
                        ?? throw new InvalidOperationException($"Sahne {scene.SceneNumber} görsel üretimi tamamlandı ancak geçerli seçili görsel bulunamadı.");
                    await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Completed, generatedAsset.Id, null, false, token);
                },
                (ex, attempt, token) => _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Failed, null, ex.Message, false, token),
                cancellationToken);

            await UpdatePipelineProgressAsync(runId, "Sahne görseli tamamlandı.", 20, 45, index + 1, workItems.Count, cancellationToken);
        }
    }

    private static bool ShouldRunStep(
        AutonomousGenerationRunStatus resumeStatus,
        AutonomousGenerationRunStatus stepStatus) =>
        resumeStatus == AutonomousGenerationRunStatus.Pending ||
        resumeStatus == AutonomousGenerationRunStatus.Validating ||
        (resumeStatus <= stepStatus &&
         resumeStatus is not AutonomousGenerationRunStatus.Paused and
             not AutonomousGenerationRunStatus.CancelRequested and
             not AutonomousGenerationRunStatus.Cancelled and
             not AutonomousGenerationRunStatus.Completed and
             not AutonomousGenerationRunStatus.Failed);

    private async Task GenerateVideosAsync(
        int runId,
        string? workerId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingVideos, "Sahne videoları üretiliyor.", cancellationToken);
        var workItems = await _runService.EnsureSceneWorkItemsAsync(runId, cancellationToken);
        var scenes = (await _runService.GetScenesAsync(snapshot.FilmProjectId, cancellationToken)).ToDictionary(scene => scene.Id);
        for (var index = 0; index < workItems.Count; index++)
        {
            var workItem = workItems[index];
            if (!await EnsureCanContinueAsync(runId, workerId, cancellationToken))
            {
                return;
            }

            var scene = scenes[workItem.StorySceneId];
            await _runService.SetCurrentSceneAsync(runId, scene.Id, scene.SceneNumber, cancellationToken);
            var existingVideo = await _runService.FindValidSelectedVideoAssetAsync(scene.Id, cancellationToken);
            if (existingVideo is not null)
            {
                await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Skipped, existingVideo.Id, null, false, cancellationToken);
                await UpdatePipelineProgressAsync(runId, "Geçerli video bulundu; yeniden üretilmedi.", 45, 80, index + 1, workItems.Count, cancellationToken);
                continue;
            }

            var sourceImage = await _runService.FindValidSelectedImageAssetAsync(scene.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Sahne {scene.SceneNumber} için geçerli seçili görsel yok; video üretimi başlatılmadı.");

            await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, null, true, cancellationToken);
            await _retryPolicy.ExecuteAsync(
                async (_, token) =>
                {
                    var request = await _videoGenerationRequestFactory.CreateAsync(new VideoGenerationRequestFactoryInput
                    {
                        FilmProjectId = snapshot.FilmProjectId,
                        Scene = scene,
                        SourceImageAsset = sourceImage,
                        ModelType = snapshot.VideoModelType,
                        Resolution = snapshot.Resolution,
                        InferenceSteps = snapshot.VideoInferenceSteps,
                        Seed = snapshot.Seed,
                        RandomSeed = snapshot.RandomSeed,
                        PreferNativeDialogue = snapshot.PreferLtxNativeDialogue
                    }, token);
                    request.StopOnFailure = true;

                    await _videoGenerationService.GenerateSceneVideoAsync(request, BuildMediaProgress(runId, 45, 80), token);
                    await EnsureWorkerOwnershipAsync(runId, workerId, token);
                    var generatedVideo = await _runService.FindValidSelectedVideoAssetAsync(scene.Id, token)
                        ?? throw new InvalidOperationException($"Sahne {scene.SceneNumber} video üretimi tamamlandı ancak geçerli seçili video bulunamadı.");
                    await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Completed, generatedVideo.Id, null, false, token);
                },
                (ex, attempt, token) => _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Failed, null, ex.Message, false, token),
                cancellationToken);

            await UpdatePipelineProgressAsync(runId, "Sahne videosu tamamlandı.", 45, 80, index + 1, workItems.Count, cancellationToken);
        }
    }

    private async Task GenerateAudioAsync(
        int runId,
        string? workerId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await TransitionIfAllowedAsync(runId, snapshot.GenerateAudio ? AutonomousGenerationRunStatus.GeneratingAudio : AutonomousGenerationRunStatus.Finalizing, "Ses aşaması kontrol ediliyor.", cancellationToken);
        var workItems = await _runService.EnsureSceneWorkItemsAsync(runId, cancellationToken);
        if (!snapshot.GenerateAudio)
        {
            foreach (var item in workItems)
            {
                await _runService.MarkWorkItemAudioAsync(item.Id, AutonomousWorkItemStatus.Skipped, null, null, false, cancellationToken);
            }

            return;
        }

        foreach (var workItem in workItems)
        {
            if (!await EnsureCanContinueAsync(runId, workerId, cancellationToken))
            {
                return;
            }

            var selectedVideo = await _runService.FindValidSelectedVideoAssetAsync(workItem.StorySceneId, cancellationToken);
            if (selectedVideo?.Role is MediaAssetRole.GeneratedNativeDialogueVideo or MediaAssetRole.FinalDialogueVideo)
            {
                await _runService.MarkWorkItemAudioAsync(workItem.Id, AutonomousWorkItemStatus.Skipped, null, null, false, cancellationToken);
                continue;
            }

            var existingAudio = await _runService.FindValidSceneAudioAssetAsync(workItem.StorySceneId, cancellationToken);
            if (existingAudio is not null)
            {
                await _runService.MarkWorkItemAudioAsync(workItem.Id, AutonomousWorkItemStatus.Skipped, existingAudio.Id, null, false, cancellationToken);
                continue;
            }

            await _runService.MarkWorkItemAudioAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, null, true, cancellationToken);
            var segments = await _runService.GetSpeechSegmentsAsync(workItem.StorySceneId, cancellationToken);
            if (segments.Count == 0)
            {
                await _audioGenerationService.CreateBasicSpeechPlanAsync(workItem.StorySceneId, cancellationToken);
                segments = await _runService.GetSpeechSegmentsAsync(workItem.StorySceneId, cancellationToken);
            }

            foreach (var segment in segments.Where(segment => segment.Status != SpeechSegmentStatus.Completed))
            {
                await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
                await _audioGenerationService.GenerateSpeechSegmentAsync(segment.Id, cancellationToken);
            }

            await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
            var speechTrack = await _audioGenerationService.CreateSpeechTrackForSceneAsync(workItem.StorySceneId, cancellationToken);
            await _runService.MarkWorkItemAudioAsync(workItem.Id, AutonomousWorkItemStatus.Completed, speechTrack.Id, null, false, cancellationToken);
        }
    }

    private async Task FinalizeAsync(
        int runId,
        string? workerId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
        await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.Finalizing, "Final çıktı aşaması hazırlanıyor.", cancellationToken);
        var workItems = await _runService.EnsureSceneWorkItemsAsync(runId, cancellationToken);
        foreach (var item in workItems)
        {
            await _runService.MarkWorkItemFinalizationAsync(item.Id, AutonomousWorkItemStatus.Completed, null, cancellationToken);
        }

        var hasNativeDialogueVideo = false;
        foreach (var item in workItems)
        {
            var selectedVideo = await _runService.FindValidSelectedVideoAssetAsync(item.StorySceneId, cancellationToken);
            if (selectedVideo?.Role == MediaAssetRole.GeneratedNativeDialogueVideo)
            {
                hasNativeDialogueVideo = true;
                break;
            }
        }

        if (hasNativeDialogueVideo)
        {
            await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
            await _finalMovieAssemblyService.AssembleLtxNativeDialogueMovieAsync(snapshot.FilmProjectId, cancellationToken);
        }
    }

    private async Task ValidateProjectAsync(
        int runId,
        FilmProject project,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (project.Id != snapshot.FilmProjectId)
        {
            throw new InvalidOperationException("Otonom çalışma snapshot proje kimliği ile kayıtlı proje eşleşmiyor.");
        }

        if (project.CalculatedClipCount <= 0)
        {
            throw new InvalidOperationException("Otonom üretim için hesaplanmış sahne sayısı sıfır olamaz.");
        }

        await _runService.MarkHeartbeatAsync(runId, "Proje yapılandırması doğrulandı.", cancellationToken: cancellationToken);
    }

    private async Task<bool> EnsureCanContinueAsync(int runId, string? workerId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
        var run = await RequireRunAsync(runId, cancellationToken);
        if (run.Status == AutonomousGenerationRunStatus.Paused)
        {
            return false;
        }

        if (run.CancellationRequested || run.Status == AutonomousGenerationRunStatus.CancelRequested)
        {
            await TryCancelRunAsync(runId, "Otonom üretim iptal edildi.", cancellationToken);
            return false;
        }

        return true;
    }

    private async Task RunLeaseRenewalLoopAsync(int runId, string workerId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.HeartbeatInterval, cancellationToken);
                var renewed = await _runService.TryRenewLeaseAsync(
                    runId,
                    workerId,
                    _options.LeaseExtension,
                    "Otonom run lease yenilendi.",
                    cancellationToken);
                if (!renewed)
                {
                    _logger.LogWarning("Autonomous run {RunId} lease renewal failed for worker {WorkerId}; ownership may be lost.", runId, workerId);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autonomous run {RunId} lease renewal loop failed.", runId);
        }
    }

    private async Task StopLeaseRenewalAsync(
        int runId,
        string? workerId,
        CancellationTokenSource renewalCancellation,
        Task renewalTask)
    {
        try
        {
            renewalCancellation.Cancel();
            await renewalTask;
            if (!string.IsNullOrWhiteSpace(workerId))
            {
                await _runService.ReleaseClaimAsync(runId, workerId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autonomous run {RunId} lease renewal shutdown failed.", runId);
        }
    }

    private async Task EnsureWorkerOwnershipAsync(int runId, string? workerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return;
        }

        if (!await _runService.IsRunOwnedByWorkerAsync(runId, workerId, cancellationToken))
        {
            throw new WorkerOwnershipLostException(runId, workerId);
        }
    }

    private sealed class WorkerOwnershipLostException : Exception
    {
        public WorkerOwnershipLostException(int runId, string workerId)
            : base($"Otonom run {runId} worker ownership kaybedildi. WorkerId={workerId}")
        {
        }
    }

    private async Task TryCancelRunAsync(int runId, string message, CancellationToken cancellationToken)
    {
        try
        {
            var run = await RequireRunAsync(runId, cancellationToken);
            if (run.Status != AutonomousGenerationRunStatus.CancelRequested)
            {
                await _runService.RequestCancellationAsync(runId, cancellationToken);
            }

            await _runService.TransitionAsync(runId, AutonomousGenerationRunStatus.Cancelled, message, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Autonomous run {RunId} cancellation marking failed.", runId);
        }
    }

    private async Task TransitionIfAllowedAsync(
        int runId,
        AutonomousGenerationRunStatus status,
        string message,
        CancellationToken cancellationToken)
    {
        var run = await RequireRunAsync(runId, cancellationToken);
        if (run.Status == status)
        {
            await _runService.MarkHeartbeatAsync(runId, message, cancellationToken: cancellationToken);
            return;
        }

        if (run.Status == AutonomousGenerationRunStatus.Paused)
        {
            return;
        }

        if (run.Status == AutonomousGenerationRunStatus.CancelRequested || run.CancellationRequested)
        {
            await TryCancelRunAsync(runId, message, cancellationToken);
            return;
        }

        try
        {
            await _runService.TransitionAsync(runId, status, message, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            await _runService.MarkHeartbeatAsync(runId, message, cancellationToken: cancellationToken);
        }
    }

    private IProgress<MediaGenerationProgress> BuildMediaProgress(int runId, double basePercentage, double targetPercentage) =>
        new Progress<MediaGenerationProgress>(progress =>
        {
            var sceneProgress = progress.OverallProgress > 0 ? progress.OverallProgress : progress.SceneProgress;
            var mapped = basePercentage + ((targetPercentage - basePercentage) * Math.Clamp(sceneProgress, 0, 100) / 100);
            _ = _runService.MarkHeartbeatAsync(runId, progress.Message, mapped, CancellationToken.None);
        });

    private Task UpdatePipelineProgressAsync(
        int runId,
        string message,
        double basePercentage,
        double targetPercentage,
        int completedItems,
        int totalItems,
        CancellationToken cancellationToken)
    {
        var fraction = totalItems == 0 ? 0 : completedItems / (double)totalItems;
        var mapped = basePercentage + ((targetPercentage - basePercentage) * fraction);
        return _runService.MarkHeartbeatAsync(runId, message, mapped, cancellationToken);
    }

    private async Task<AutonomousGenerationRun> RequireRunAsync(int runId, CancellationToken cancellationToken) =>
        await _runService.GetRunAsync(runId, cancellationToken)
        ?? throw new InvalidOperationException("Otonom çalışma bulunamadı.");

    private static AutonomousGenerationConfigurationSnapshot DeserializeSnapshot(AutonomousGenerationRun run, FilmProject project)
    {
        if (!string.IsNullOrWhiteSpace(run.ConfigurationSnapshotJson))
        {
            var snapshot = JsonSerializer.Deserialize<AutonomousGenerationConfigurationSnapshot>(run.ConfigurationSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return new AutonomousGenerationConfigurationSnapshot
        {
            FilmProjectId = project.Id,
            ProjectName = project.ProjectName,
            Subject = project.Subject,
            TotalDurationMinutes = project.TotalDurationMinutes,
            ClipDurationSeconds = project.ClipDurationSeconds,
            CalculatedClipCount = project.CalculatedClipCount,
            Language = project.Language,
            TargetAudience = project.TargetAudience,
            StoryGenre = project.StoryGenre,
            VisualStyle = project.VisualStyle,
            VideoStyle = project.VideoStyle,
            AspectRatio = project.AspectRatio,
            Resolution = project.Resolution,
            UseNarrator = project.UseNarrator,
            NarratorTone = project.NarratorTone,
            MainCharacterDescription = project.MainCharacterDescription,
            AdditionalInstructions = project.AdditionalInstructions
        };
    }
}
