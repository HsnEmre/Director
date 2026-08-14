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
    private readonly IWanGpRuntimeCoordinator _wanGpRuntimeCoordinator;
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
        IWanGpRuntimeCoordinator wanGpRuntimeCoordinator,
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
        _wanGpRuntimeCoordinator = wanGpRuntimeCoordinator;
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

            if (UseStagedPipeline())
            {
                var checkpointResumeStatus = resumeStatus is AutonomousGenerationRunStatus.Pending or AutonomousGenerationRunStatus.Validating
                    ? await DetermineCheckpointResumeStatusAsync(project.Id, snapshot, cancellationToken)
                    : resumeStatus;
                await RunStagedPipelineAsync(runId, workerId, checkpointResumeStatus, project, snapshot, cancellationToken);
                return;
            }
            else if (ShouldRunStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingScenes))
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

    private async Task RunStagedPipelineAsync(
        int runId,
        string? workerId,
        AutonomousGenerationRunStatus resumeStatus,
        FilmProject project,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingStoryNarrative))
        {
            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingStoryNarrative, "Story narrative checkpoint is being generated.", cancellationToken);
            await _storyGenerationService.GenerateStoryNarrativeAsync(project.Id, BuildStoryProgress(runId, 0, 10), cancellationToken);
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingCharacters))
        {
            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingCharacters, "Character continuity checkpoint is being generated.", cancellationToken);
            await _storyGenerationService.GenerateStoryCharactersAsync(project.Id, BuildStoryProgress(runId, 10, 18), cancellationToken);
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingNarrativeScenes))
        {
            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingNarrativeScenes, "Narrative scenes are being generated one at a time.", cancellationToken);
            await _storyGenerationService.GenerateAllMissingNarrativeScenesAsync(project.Id, BuildStoryProgress(runId, 18, 30), cancellationToken);
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingImagePrompts))
        {
            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingImagePrompts, "Image prompts are being generated after narrative scenes.", cancellationToken);
            await _storyGenerationService.GenerateAllMissingImagePromptsAsync(project.Id, BuildStoryProgress(runId, 30, 35), cancellationToken);
        }

        var workItems = await _runService.EnsureSceneWorkItemsAsync(runId, cancellationToken);
        if (workItems.Count == 0)
        {
            throw new InvalidOperationException("No scenes found for autonomous generation.");
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingVideoPrompts) ||
            await HasMissingVideoPromptsAsync(snapshot.FilmProjectId, cancellationToken))
        {
            await EnsureAllScenesHaveImagePromptsAsync(snapshot.FilmProjectId, cancellationToken);
            await TransitionIfAllowedAsync(runId, AutonomousGenerationRunStatus.GeneratingVideoPrompts, "Video prompts are being generated after image prompts and before image generation.", cancellationToken);
            await _storyGenerationService.GenerateAllMissingVideoPromptsAsync(project.Id, BuildStoryProgress(runId, 35, 45), cancellationToken);
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingImages))
        {
            await EnsureAllScenesHaveImagePromptsAsync(snapshot.FilmProjectId, cancellationToken);
            await EnsureAllScenesHaveVideoPromptsAsync(snapshot.FilmProjectId, cancellationToken);
            await GenerateImagesAsync(runId, workerId, snapshot, workItems, cancellationToken);
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingVideos))
        {
            await EnsureAllScenesHaveVideoPromptsAsync(snapshot.FilmProjectId, cancellationToken);
            await GenerateVideosAsync(runId, workerId, snapshot, cancellationToken);
        }

        if (ShouldRunPipelineStep(resumeStatus, AutonomousGenerationRunStatus.GeneratingAudio))
        {
            await EnsureAllScenesHaveSelectedVideosAsync(snapshot.FilmProjectId, cancellationToken);
            await GenerateAudioAsync(runId, workerId, snapshot, cancellationToken);
        }

        await FinalizeAsync(runId, workerId, snapshot, cancellationToken);
        await EnsureWorkerOwnershipAsync(runId, workerId, cancellationToken);
        await _runService.CompleteRunAsync(runId, "Autonomous generation completed.", cancellationToken);
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
            if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
            {
                throw new InvalidOperationException($"Scene {scene.SceneNumber} image prompt is empty; image generation did not start.");
            }

            var existingAsset = await ReconcileExistingImageAsync(workItem, scene, cancellationToken);
            if (existingAsset is not null)
            {
                await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Completed, existingAsset.Id, null, false, cancellationToken);
                await UpdatePipelineProgressAsync(runId, "Geçerli görsel bulundu; yeniden üretilmedi.", 20, 45, index + 1, workItems.Count, cancellationToken);
                continue;
            }

            if (await _runService.HasActiveGenerationJobAsync(scene.Id, MediaType.Image, cancellationToken))
            {
                await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, "Existing active image generation job found.", false, cancellationToken);
                throw new InvalidOperationException($"Scene {scene.SceneNumber} already has an active image generation job; duplicate job was not created.");
            }

            await _retryPolicy.ExecuteAsync(
                async (attempt, token) =>
                {
                    await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, null, true, token);
                    await EnsureWanGpReadyForMediaAttemptAsync(runId, scene.SceneNumber, MediaType.Image, attempt, token);
                    var request = new WanGpImageGenerationRequest
                    {
                        ModelType = string.IsNullOrWhiteSpace(snapshot.ImageModelType) ? "qwen_image_20B" : snapshot.ImageModelType,
                        Prompt = scene.ImagePrompt,
                        NegativePrompt = scene.ImageNegativePrompt,
                        Resolution = snapshot.Resolution,
                        InferenceSteps = snapshot.ImageInferenceSteps,
                        Seed = snapshot.Seed,
                        RandomSeed = snapshot.RandomSeed,
                        StopOnError = true,
                        AutoSelectOutput = true
                    };

                    var previousImage = await ResolvePreviousSceneImageReferenceAsync(scene, scenes.Values, token);
                    if (previousImage is not null)
                    {
                        request.SourceImageAssetId = previousImage.Id;
                        request.SourceImagePath = previousImage.FilePath;
                    }

                    await _imageGenerationService.GenerateSceneImageAsync(scene.Id, request, BuildMediaProgress(runId, 20, 45), token);
                    await EnsureWorkerOwnershipAsync(runId, workerId, token);
                    var generatedAsset = await ReconcileExistingImageAsync(workItem, scene, token)
                        ?? throw new InvalidOperationException($"Sahne {scene.SceneNumber} görsel üretimi tamamlandı ancak geçerli seçili görsel bulunamadı.");
                    await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Completed, generatedAsset.Id, null, false, token);
                },
                (ex, attempt, token) => OnMediaFailedAttemptAsync(runId, workItem.Id, scene.SceneNumber, MediaType.Image, ex, attempt, token),
                cancellationToken);

            await UpdatePipelineProgressAsync(runId, "Sahne görseli tamamlandı.", 20, 45, index + 1, workItems.Count, cancellationToken);
        }
    }

    private async Task EnsureWanGpReadyForMediaAttemptAsync(
        int runId,
        int sceneNumber,
        MediaType mediaType,
        int attempt,
        CancellationToken cancellationToken)
    {
        var status = await _wanGpRuntimeCoordinator.EnsureReadyAsync(cancellationToken);
        var mediaLabel = mediaType == MediaType.Video ? "video" : "görsel";
        if (status.IsReady)
        {
            if (attempt > 1)
            {
                await _runService.MarkHeartbeatAsync(
                    runId,
                    $"{sceneNumber}. sahne {mediaLabel} üretimi için WanGP MCP yeniden hazır; deneme {attempt}.",
                    cancellationToken: cancellationToken);
            }

            return;
        }

        throw new InvalidOperationException(
            $"WanGP MCP runtime {sceneNumber}. sahne {mediaLabel} üretimi için hazır değil. Durum={status.McpState}; Mesaj={status.Message}");
    }

    private async Task OnMediaFailedAttemptAsync(
        int runId,
        int workItemId,
        int sceneNumber,
        MediaType mediaType,
        Exception exception,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (mediaType == MediaType.Video)
        {
            await _runService.MarkWorkItemVideoAsync(workItemId, AutonomousWorkItemStatus.Failed, null, exception.Message, false, cancellationToken);
        }
        else
        {
            await _runService.MarkWorkItemImageAsync(workItemId, AutonomousWorkItemStatus.Failed, null, exception.Message, false, cancellationToken);
        }

        var mediaLabel = mediaType == MediaType.Video ? "video" : "görsel";
        await _runService.MarkHeartbeatAsync(
            runId,
            $"{sceneNumber}. sahne {mediaLabel} üretimi {attempt}. denemede hata aldı; WanGP MCP yeniden doğrulanıp aynı sahne tekrar denenecek. {exception.Message}",
            cancellationToken: cancellationToken);

        try
        {
            await _wanGpRuntimeCoordinator.EnsureReadyAsync(cancellationToken);
        }
        catch (Exception recoveryException) when (recoveryException is not OperationCanceledException)
        {
            _logger.LogWarning(
                recoveryException,
                "WanGP runtime recovery after failed media attempt did not complete. Scene={SceneNumber}; MediaType={MediaType}; Attempt={Attempt}",
                sceneNumber,
                mediaType,
                attempt);
        }

        if (_options.MediaRetryDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.MediaRetryDelay, cancellationToken);
        }
    }

    private async Task<SceneMediaAsset?> ReconcileExistingImageAsync(
        AutonomousSceneWorkItem workItem,
        FilmScene scene,
        CancellationToken cancellationToken)
    {
        var selected = await _runService.FindValidSelectedImageAssetAsync(scene.Id, cancellationToken);
        if (selected is not null)
        {
            if (workItem.ImageMediaAssetId != selected.Id)
            {
                await _runService.MarkWorkItemImageAsync(workItem.Id, workItem.ImageStatus, selected.Id, null, false, cancellationToken);
            }

            return selected;
        }

        var validAsset = await _runService.FindValidImageAssetAsync(scene.Id, cancellationToken);
        if (validAsset is null)
        {
            return null;
        }

        await _imageGenerationService.SetSelectedAssetAsync(validAsset.Id, cancellationToken);
        selected = await _runService.FindValidSelectedImageAssetAsync(scene.Id, cancellationToken) ?? validAsset;
        await _runService.MarkWorkItemImageAsync(workItem.Id, AutonomousWorkItemStatus.Completed, selected.Id, null, false, cancellationToken);
        return selected;
    }

    private async Task<SceneMediaAsset?> ResolvePreviousSceneImageReferenceAsync(
        FilmScene scene,
        IEnumerable<FilmScene> allScenes,
        CancellationToken cancellationToken)
    {
        var previousScene = allScenes
            .Where(candidate => candidate.SceneNumber < scene.SceneNumber)
            .OrderByDescending(candidate => candidate.SceneNumber)
            .FirstOrDefault();
        if (previousScene is null)
        {
            return null;
        }

        return await _runService.FindValidSelectedImageAssetAsync(previousScene.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Scene {scene.SceneNumber} image generation cannot start before scene {previousScene.SceneNumber} has a valid selected image reference.");
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

    private static bool UseStagedPipeline() => true;

    private async Task<AutonomousGenerationRunStatus> DetermineCheckpointResumeStatusAsync(
        int filmProjectId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var checkpoint = await _runService.GetProjectCheckpointAsync(filmProjectId, cancellationToken);
        if (!checkpoint.HasValidStory)
        {
            return AutonomousGenerationRunStatus.GeneratingStoryNarrative;
        }

        if (!checkpoint.HasValidCharacters)
        {
            return AutonomousGenerationRunStatus.GeneratingCharacters;
        }

        if (checkpoint.FirstMissingNarrativeSceneNumber is not null)
        {
            return AutonomousGenerationRunStatus.GeneratingNarrativeScenes;
        }

        if (checkpoint.FirstMissingImagePromptSceneNumber is not null)
        {
            return AutonomousGenerationRunStatus.GeneratingImagePrompts;
        }

        if (checkpoint.FirstMissingVideoPromptSceneNumber is not null)
        {
            return AutonomousGenerationRunStatus.GeneratingVideoPrompts;
        }

        if (checkpoint.FirstMissingSelectedImageSceneNumber is not null)
        {
            return AutonomousGenerationRunStatus.GeneratingImages;
        }

        if (checkpoint.FirstMissingSelectedVideoSceneNumber is not null)
        {
            return AutonomousGenerationRunStatus.GeneratingVideos;
        }

        if (snapshot.GenerateAudio && checkpoint.FirstMissingSceneAudioSceneNumber is not null)
        {
            return AutonomousGenerationRunStatus.GeneratingAudio;
        }

        return AutonomousGenerationRunStatus.Finalizing;
    }

    private static bool ShouldRunPipelineStep(
        AutonomousGenerationRunStatus resumeStatus,
        AutonomousGenerationRunStatus stepStatus)
    {
        if (resumeStatus is AutonomousGenerationRunStatus.Completed or
            AutonomousGenerationRunStatus.Failed or
            AutonomousGenerationRunStatus.Cancelled or
            AutonomousGenerationRunStatus.Paused or
            AutonomousGenerationRunStatus.CancelRequested)
        {
            return false;
        }

        return PipelineIndex(resumeStatus) <= PipelineIndex(stepStatus);
    }

    private static int PipelineIndex(AutonomousGenerationRunStatus status) => status switch
    {
        AutonomousGenerationRunStatus.Pending => 0,
        AutonomousGenerationRunStatus.Validating => 0,
        AutonomousGenerationRunStatus.GeneratingStory => 0,
        AutonomousGenerationRunStatus.GeneratingStoryNarrative => 0,
        AutonomousGenerationRunStatus.GeneratingCharacters => 1,
        AutonomousGenerationRunStatus.GeneratingScenes => 2,
        AutonomousGenerationRunStatus.GeneratingNarrativeScenes => 2,
        AutonomousGenerationRunStatus.GeneratingImagePrompts => 3,
        AutonomousGenerationRunStatus.GeneratingVideoPrompts => 4,
        AutonomousGenerationRunStatus.GeneratingImages => 5,
        AutonomousGenerationRunStatus.GeneratingVideos => 6,
        AutonomousGenerationRunStatus.GeneratingAudio => 7,
        AutonomousGenerationRunStatus.Finalizing => 8,
        _ => 99
    };

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
            if (string.IsNullOrWhiteSpace(scene.VideoPrompt))
            {
                throw new InvalidOperationException($"Scene {scene.SceneNumber} video prompt is empty; video generation did not start.");
            }

            var existingVideo = await _runService.FindValidSelectedVideoAssetAsync(scene.Id, cancellationToken);
            if (existingVideo is not null)
            {
                await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Skipped, existingVideo.Id, null, false, cancellationToken);
                await UpdatePipelineProgressAsync(runId, "Geçerli video bulundu; yeniden üretilmedi.", 45, 80, index + 1, workItems.Count, cancellationToken);
                continue;
            }

            if (await _runService.HasActiveGenerationJobAsync(scene.Id, MediaType.Video, cancellationToken))
            {
                await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, "Existing active video generation job found.", false, cancellationToken);
                throw new InvalidOperationException($"Scene {scene.SceneNumber} already has an active video generation job; duplicate job was not created.");
            }

            var sourceImage = await _runService.FindValidSelectedImageAssetAsync(scene.Id, cancellationToken)
                ?? throw new InvalidOperationException($"Sahne {scene.SceneNumber} için geçerli seçili görsel yok; video üretimi başlatılmadı.");

            await _retryPolicy.ExecuteAsync(
                async (attempt, token) =>
                {
                    var reconciledVideo = await _runService.FindValidSelectedVideoAssetAsync(scene.Id, token);
                    if (reconciledVideo is not null)
                    {
                        await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Completed, reconciledVideo.Id, null, false, token);
                        return;
                    }

                    await _runService.MarkWorkItemVideoAsync(workItem.Id, AutonomousWorkItemStatus.Running, null, null, true, token);
                    await EnsureWanGpReadyForMediaAttemptAsync(runId, scene.SceneNumber, MediaType.Video, attempt, token);
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
                (ex, attempt, token) => OnMediaFailedAttemptAsync(runId, workItem.Id, scene.SceneNumber, MediaType.Video, ex, attempt, token),
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

    private IProgress<StoryGenerationProgress> BuildStoryProgress(int runId, double basePercentage, double targetPercentage) =>
        new Progress<StoryGenerationProgress>(progress =>
        {
            var source = progress.Percentage > 0 ? progress.Percentage : 0;
            var mapped = basePercentage + ((targetPercentage - basePercentage) * Math.Clamp(source, 0, 100) / 100);
            _ = _runService.MarkHeartbeatAsync(runId, progress.Message, mapped, CancellationToken.None);
        });

    private async Task EnsureAllScenesHaveImagePromptsAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        var missing = (await _runService.GetScenesAsync(filmProjectId, cancellationToken))
            .Where(scene => string.IsNullOrWhiteSpace(scene.ImagePrompt) || string.IsNullOrWhiteSpace(scene.ImageNegativePrompt))
            .Select(scene => scene.SceneNumber)
            .FirstOrDefault();
        if (missing > 0)
        {
            throw new InvalidOperationException($"Image generation cannot start before every image prompt is saved. First missing scene={missing}.");
        }
    }

    private async Task EnsureAllScenesHaveVideoPromptsAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        var missing = (await _runService.GetScenesAsync(filmProjectId, cancellationToken))
            .Where(scene =>
                string.IsNullOrWhiteSpace(scene.VideoPrompt) ||
                string.IsNullOrWhiteSpace(scene.VideoNegativePrompt) ||
                StoryGenerationService.HasInvalidSilentVideoPromptFields(scene.VideoPrompt, scene.VideoNegativePrompt))
            .Select(scene => scene.SceneNumber)
            .FirstOrDefault();
        if (missing > 0)
        {
            throw new InvalidOperationException($"Video generation cannot start before every video prompt is saved. First missing scene={missing}.");
        }
    }

    private async Task<bool> HasMissingVideoPromptsAsync(int filmProjectId, CancellationToken cancellationToken) =>
        (await _runService.GetScenesAsync(filmProjectId, cancellationToken))
            .Any(scene =>
                string.IsNullOrWhiteSpace(scene.VideoPrompt) ||
                string.IsNullOrWhiteSpace(scene.VideoNegativePrompt) ||
                StoryGenerationService.HasInvalidSilentVideoPromptFields(scene.VideoPrompt, scene.VideoNegativePrompt));

    private async Task EnsureAllScenesHaveSelectedImagesAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        foreach (var scene in await _runService.GetScenesAsync(filmProjectId, cancellationToken))
        {
            if (await _runService.FindValidSelectedImageAssetAsync(scene.Id, cancellationToken) is null)
            {
                throw new InvalidOperationException($"Video prompt generation cannot start before every scene has a valid selected image. First missing scene={scene.SceneNumber}.");
            }
        }
    }

    private async Task EnsureAllScenesHaveSelectedVideosAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        foreach (var scene in await _runService.GetScenesAsync(filmProjectId, cancellationToken))
        {
            if (await _runService.FindValidSelectedVideoAssetAsync(scene.Id, cancellationToken) is null)
            {
                throw new InvalidOperationException($"Audio generation cannot start before every scene has a valid selected video. First missing scene={scene.SceneNumber}.");
            }
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
