using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using Director.Data;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class VideoGenerationService : IVideoGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpVideoRequestBuilder _requestBuilder;
    private readonly IWanGpVideoOutputResolver _outputResolver;
    private readonly IVideoMetadataService _metadataService;
    private readonly IMediaFileService _mediaFileService;
    private readonly IApplicationActivityCenter _activityCenter;
    private readonly OllamaOptions _ollamaOptions;
    private readonly ILogger<VideoGenerationService> _logger;
    private readonly object _activeJobLock = new();
    private string? _activeExternalJobId;

    public VideoGenerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IGpuGenerationCoordinator gpuCoordinator,
        IWanGpClient wanGpClient,
        IWanGpVideoRequestBuilder requestBuilder,
        IWanGpVideoOutputResolver outputResolver,
        IVideoMetadataService metadataService,
        IMediaFileService mediaFileService,
        IApplicationActivityCenter activityCenter,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<VideoGenerationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _gpuCoordinator = gpuCoordinator;
        _wanGpClient = wanGpClient;
        _requestBuilder = requestBuilder;
        _outputResolver = outputResolver;
        _metadataService = metadataService;
        _mediaFileService = mediaFileService;
        _activityCenter = activityCenter;
        _ollamaOptions = ollamaOptions.Value;
        _logger = logger;
    }

    public async Task<GenerationJob> GenerateSceneVideoAsync(
        WanGpVideoGenerationRequest request,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scene = await LoadSceneAsync(request.SceneId, cancellationToken);
        GenerationJob? job = null;
        try
        {
            var reference = await LoadSelectedReferenceAssetAsync(scene, request.SourceImageAssetId, cancellationToken);
            var beforeSnapshot = _outputResolver.CaptureSnapshot();
            var build = await _requestBuilder.BuildAsync(request, cancellationToken);
            AssertImageToVideoRequest(request, build, reference);
            _activityCenter.AddLog("Video", "Referans gorsel WanGP Start Image alanina eklendi.", GenerationLogLevel.Success);
            job = await CreateJobAsync(scene, reference.Id, request, build, cancellationToken);
            _activityCenter.SetActiveJob(job.Id, null);

            WanGpGenerationSubmission submission;
            WanGpJobSnapshot snapshot;
            await using (var gpuLease = await _gpuCoordinator.AcquireAsync(
                GenerationOperationType.Video,
                scene.FilmProjectId,
                scene.Id,
                cancellationToken))
            {
            submission = await _wanGpClient.SubmitVideoGenerationAsync(build.Source, cancellationToken);
            if (string.IsNullOrWhiteSpace(submission.ExternalJobId))
            {
                throw new InvalidOperationException("WanGP video job id dondurmedi.");
            }

            lock (_activeJobLock)
            {
                _activeExternalJobId = submission.ExternalJobId;
            }

            _activityCenter.SetActiveJob(job.Id, submission.ExternalJobId);
            await UpdateJobAsync(job.Id, existing =>
            {
                existing.ExternalJobId = submission.ExternalJobId;
                existing.Status = GenerationJobStatus.Running;
                existing.StartedAt = DateTime.Now;
                existing.CurrentPhase = "VideoGenerating";
                existing.UpdatedAt = DateTime.Now;
            }, cancellationToken);

            progress?.Report(new MediaGenerationProgress { Phase = "VideoGenerating", Message = $"Sahne {scene.SceneNumber} video uretimi baslatildi.", OverallProgress = 5, ExternalJobId = submission.ExternalJobId });

            snapshot = await PollUntilVideoOutputAsync(job.Id, submission.ExternalJobId, scene.SceneNumber, beforeSnapshot, job.StartedAt ?? job.CreatedAt, progress, cancellationToken);
            }
            if (snapshot.Status != GenerationJobStatus.Completed)
            {
                await UpdateJobAsync(job.Id, existing =>
                {
                    existing.Status = snapshot.Status;
                    existing.ErrorMessage = snapshot.Message;
                    existing.CompletedAt = DateTime.Now;
                    existing.UpdatedAt = DateTime.Now;
                }, cancellationToken);
                return await LoadJobAsync(job.Id, cancellationToken);
            }

            var outputPath = snapshot.GeneratedFiles.FirstOrDefault() ?? snapshot.OutputPath
                ?? throw new InvalidOperationException("WanGP video output dosyasi bulunamadi.");
            cancellationToken.ThrowIfCancellationRequested();
            var asset = await SaveCompletedVideoAssetAsync(scene.Id, job.Id, reference.Id, outputPath, request, cancellationToken);
            WriteOutputSummary(submission.ExternalJobId, "generated_files", outputPath, asset);
            progress?.Report(new MediaGenerationProgress
            {
                Phase = "Completed",
                Message = $"Sahne {scene.SceneNumber} videosu hazir: v{asset.VersionNumber}",
                OverallProgress = 100,
                SceneProgress = 100,
                CurrentSceneNumber = scene.SceneNumber,
                ModelType = request.ModelType,
                PreviewPath = asset.FilePath
            });
            _activityCenter.AddLog("Video", $"Sahne {scene.SceneNumber} videosu hazir.", GenerationLogLevel.Success);
            return await LoadCompletedJobAsync(job.Id, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (job is not null)
            {
                await MarkJobCancelledBestEffortAsync(job.Id);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WanGP video uretimi basarisiz oldu.");
            if (job is not null)
            {
                await MarkJobFailedBestEffortAsync(job.Id, ex.Message);
            }

            throw;
        }
        finally
        {
            lock (_activeJobLock)
            {
                _activeExternalJobId = null;
            }

            _activityCenter.AddLog("Video", "Video uretim kilidi serbest birakildi. Yeni sahne uretimine hazir.");
        }
    }

    public async Task CancelActiveJobAsync(CancellationToken cancellationToken = default)
    {
        string? externalJobId;
        lock (_activeJobLock)
        {
            externalJobId = _activeExternalJobId;
        }

        if (!string.IsNullOrWhiteSpace(externalJobId))
        {
            await _wanGpClient.CancelJobAsync(externalJobId, cancellationToken);
        }
    }

    public async Task SetSelectedVideoAssetAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.SceneMediaAssets.FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Video varligi bulunamadi.");
        var sceneAssets = await db.SceneMediaAssets
            .Where(item => item.SceneId == asset.SceneId && item.MediaType == MediaType.Video)
            .ToListAsync(cancellationToken);
        foreach (var item in sceneAssets)
        {
            item.IsSelected = item.Id == assetId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<WanGpJobSnapshot> PollUntilVideoOutputAsync(
        int jobId,
        string externalJobId,
        int sceneNumber,
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IProgress<MediaGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            var snapshot = await _wanGpClient.GetJobAsync(externalJobId, linked.Token);
            progress?.Report(new MediaGenerationProgress
            {
                Phase = string.IsNullOrWhiteSpace(snapshot.Phase) ? snapshot.Status.ToString() : snapshot.Phase,
                Message = $"Sahne {sceneNumber} video: {snapshot.Message ?? snapshot.Status.ToString()}",
                OverallProgress = snapshot.ProgressPercentage,
                CurrentStep = snapshot.CurrentStep,
                TotalSteps = snapshot.TotalSteps,
                ExternalJobId = externalJobId
            });

            var explicitPaths = snapshot.GeneratedFiles.ToList();
            if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
            {
                explicitPaths.Add(snapshot.OutputPath);
            }

            var output = await _outputResolver.ResolveVideoOutputsAsync(beforeSnapshot, startedAt, explicitPaths, TimeSpan.FromSeconds(1), linked.Token);
            if (output.Success)
            {
                var paths = output.Candidates.Select(item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                linked.Token.ThrowIfCancellationRequested();
                snapshot.Status = GenerationJobStatus.Completed;
                snapshot.Message = "VideoOutputResolvedBeforeMcpTerminalState";
                snapshot.OutputPath = paths.FirstOrDefault();
                snapshot.GeneratedFiles = paths;
                await UpdateJobAsync(jobId, job =>
                {
                    job.Status = GenerationJobStatus.Completed;
                    job.ProgressPercentage = Math.Max(job.ProgressPercentage, 95);
                    job.CurrentPhase = "VideoOutputResolving";
                    job.UpdatedAt = DateTime.Now;
                }, linked.Token);
                return snapshot;
            }

            if (output.IsAmbiguous)
            {
                snapshot.Status = GenerationJobStatus.Failed;
                snapshot.Message = output.Message;
                return snapshot;
            }

            if (snapshot.Status is GenerationJobStatus.Completed or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled or GenerationJobStatus.Interrupted)
            {
                return snapshot;
            }

            await Task.Delay(delay, linked.Token);
        }
    }

    private async Task<SceneMediaAsset> SaveCompletedVideoAssetAsync(int sceneId, int jobId, int sourceImageAssetId, string outputPath, WanGpVideoGenerationRequest request, CancellationToken cancellationToken)
    {
        FilmScene scene;
        GenerationJob job;
        SceneMediaAsset reference;
        int versionNumber;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            scene = await db.FilmScenes.AsNoTracking().FirstAsync(item => item.Id == sceneId, cancellationToken);
            job = await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == jobId, cancellationToken);
            reference = await db.SceneMediaAssets.AsNoTracking().FirstAsync(item => item.Id == sourceImageAssetId, cancellationToken);
            var existing = await db.SceneMediaAssets.Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Video).ToListAsync(cancellationToken);
            versionNumber = existing.Count == 0 ? 1 : existing.Max(item => item.VersionNumber) + 1;
        }

        var metadata = await _metadataService.ProbeAsync(outputPath, cancellationToken);
        if (request.GenerationMode == VideoAudioGenerationMode.LtxNativeDialogue)
        {
            ValidateNativeDialogueOutput(metadata);
        }

        var asset = await _mediaFileService.CopyGeneratedVideoAsync(scene, job, outputPath, metadata, versionNumber, true, sourceImageAssetId, reference.ThumbnailPath ?? reference.FilePath, cancellationToken);
        if (request.GenerationMode == VideoAudioGenerationMode.LtxNativeDialogue)
        {
            asset.Role = MediaAssetRole.GeneratedNativeDialogueVideo;
            asset.MetadataJson = BuildNativeDialogueMetadata(asset.MetadataJson, request, metadata);
        }

        ApplyDurationValidation(asset, scene.DurationSeconds, metadata.DurationSeconds);
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var previous = await db.SceneMediaAssets.Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Video).ToListAsync(cancellationToken);
            foreach (var item in previous)
            {
                item.IsSelected = false;
            }

            db.SceneMediaAssets.Add(asset);
            var trackedJob = await db.GenerationJobs.FirstAsync(item => item.Id == jobId, cancellationToken);
            trackedJob.Status = GenerationJobStatus.Completed;
            trackedJob.ProgressPercentage = 100;
            trackedJob.CurrentPhase = "Completed";
            trackedJob.CompletedAt = DateTime.Now;
            trackedJob.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return asset;
    }

    private void ApplyDurationValidation(SceneMediaAsset asset, int targetDurationSeconds, double? actualDurationSeconds)
    {
        var actual = actualDurationSeconds ?? 0;
        var mismatch = actual <= 0 || Math.Abs(actual - targetDurationSeconds) > 0.5;
        var metadata = string.IsNullOrWhiteSpace(asset.MetadataJson)
            ? new JsonObject()
            : JsonNode.Parse(asset.MetadataJson) as JsonObject ?? new JsonObject();
        metadata["TargetDurationSeconds"] = targetDurationSeconds;
        metadata["ActualDurationSeconds"] = actual;
        metadata["DurationMismatch"] = mismatch;
        asset.MetadataJson = metadata.ToJsonString(JsonOptions);
        if (mismatch)
        {
            _activityCenter.AddLog("Video", $"Video uretildi ancak hedef sureyle uyusmuyor. Hedef={targetDurationSeconds:0.0} sn; Gercek={actual:0.00} sn.", GenerationLogLevel.Warning);
        }
        else
        {
            _activityCenter.AddLog("Video", $"MP4 suresi ffprobe ile dogrulandi. Hedef={targetDurationSeconds:0.0} sn; Gercek={actual:0.00} sn.", GenerationLogLevel.Success);
        }
    }

    private void ValidateNativeDialogueOutput(VideoMetadata metadata)
    {
        var error = ValidateNativeDialogueOutputMetadata(metadata);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        _activityCenter.AddLog("Video", "Video stream dogrulandi.", GenerationLogLevel.Success);
        _activityCenter.AddLog("Video", "Audio stream dogrulandi.", GenerationLogLevel.Success);
    }

    public static string? ValidateNativeDialogueOutputMetadata(VideoMetadata metadata)
    {
        if (!metadata.HasVideo)
        {
            return "LTX native dialogue output icinde video stream bulunamadi.";
        }

        if (!metadata.HasAudio)
        {
            return "LTX native dialogue output icinde audio stream bulunamadi.";
        }

        if ((metadata.AudioDurationSeconds ?? metadata.DurationSeconds ?? 0) <= 0)
        {
            return "LTX native dialogue output audio suresi sifir.";
        }

        var duration = metadata.DurationSeconds ?? 0;
        if (duration < 9.5 || duration > 10.5)
        {
            return $"LTX native dialogue output suresi 10 saniye butcesi disinda. Sure={duration:0.00} sn.";
        }

        return null;
    }

    private async Task<FilmScene> LoadSceneAsync(int sceneId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes.AsNoTracking().FirstAsync(item => item.Id == sceneId, cancellationToken);
    }

    private async Task<SceneMediaAsset> LoadSelectedReferenceAssetAsync(FilmScene scene, int assetId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var selected = await db.SceneMediaAssets.AsNoTracking()
            .Where(item => item.SceneId == scene.Id && item.FilmProjectId == scene.FilmProjectId && item.MediaType == MediaType.Image && item.IsSelected)
            .ToListAsync(cancellationToken);
        if (selected.Count == 0)
        {
            throw new InvalidOperationException("Once bu sahne icin ana referans gorsel secin.");
        }

        if (selected.Count > 1)
        {
            throw new InvalidOperationException("Bu sahnede birden fazla secili referans gorsel var; veri butunlugu duzeltilmeden video baslatilamaz.");
        }

        var asset = selected[0];
        if (asset.Id != assetId)
        {
            throw new InvalidOperationException("Qwen ve WanGP source asset id eslesmiyor.");
        }

        if (asset.SceneId != scene.Id || asset.FilmProjectId != scene.FilmProjectId)
        {
            throw new InvalidOperationException("Referans gorsel secili sahneye/projeye ait degil.");
        }

        if (string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath))
        {
            throw new FileNotFoundException("Video referans gorseli bulunamadi.", asset.FilePath);
        }

        var info = new FileInfo(asset.FilePath);
        if (info.Length <= 0)
        {
            throw new InvalidOperationException("Video referans gorseli bos dosya.");
        }

        ValidateImageDecodable(asset.FilePath);
        return asset;
    }

    private async Task<GenerationJob> CreateJobAsync(
        FilmScene scene,
        int sourceImageAssetId,
        WanGpVideoGenerationRequest request,
        WanGpVideoRequestBuildResult build,
        CancellationToken cancellationToken)
    {
        var job = new GenerationJob
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            SourceMediaAssetId = sourceImageAssetId,
            MediaType = MediaType.Video,
            Provider = GenerationProvider.WanGp,
            Status = GenerationJobStatus.Pending,
            ModelType = request.ModelType,
            Prompt = request.Prompt,
            NegativePrompt = request.NegativePrompt,
            SettingsJson = JsonSerializer.Serialize(BuildJobSettingsSummary(request, build), JsonOptions),
            CurrentPhase = "VideoQueued",
            PromptPreparationModel = _ollamaOptions.PromptPreparationModel,
            PromptPreparedAt = DateTime.Now,
            CreatedAt = DateTime.Now
        };

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private static void AssertImageToVideoRequest(
        WanGpVideoGenerationRequest request,
        WanGpVideoRequestBuildResult build,
        SceneMediaAsset reference)
    {
        if (!build.SupportsStartImage || !build.HasStartImage)
        {
            throw new InvalidOperationException("Secili model image-to-video destekliyor ancak referans gorsel WanGP request'ine eklenemedi.");
        }

        if (build.InputContract is not { IsValidated: true, SupportsStartImage: true })
        {
            throw new InvalidOperationException("Secili model image-to-video destekliyor ancak WanGP Start Image sozlesmesi cozumlenemedi.");
        }

        if (!string.Equals(request.InputMode, "start", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Image-to-video input mode Start Image olmalidir.");
        }

        if (request.SourceImageAssetId != reference.Id)
        {
            throw new InvalidOperationException("Qwen ve WanGP source asset id eslesmiyor.");
        }

        if (!WanGpVideoRequestBuilder.TryReadStartImagePath(build.Source, build.ImageInputKey, out var imagePath) ||
            !File.Exists(imagePath) ||
            !string.Equals(Path.GetFullPath(imagePath), Path.GetFullPath(reference.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Secili model image-to-video destekliyor ancak referans gorsel WanGP request'ine eklenemedi.");
        }

        if (string.IsNullOrWhiteSpace(build.InputModeKey) ||
            !build.Source.TryGetValue(build.InputModeKey, out var inputModeValue) ||
            !string.Equals(inputModeValue?.ToString(), "S", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Image-to-video input mode Start Image icin image_prompt_type=S olmalidir.");
        }

        if (request.GenerationMode == VideoAudioGenerationMode.LtxNativeDialogue)
        {
            if (!request.NativeDialogueCapabilitySupported)
            {
                throw new InvalidOperationException("Secilen video modeli LTX native audio-video uretimi icin dogrulanamadi. " +
                    $"ModelType={request.ModelType}; Canonical={request.CanonicalModelType}; Reason={request.NativeDialogueCapabilityFailureReason}");
            }

            if (build.NativeAudioDisabledByRequest)
            {
                throw new InvalidOperationException("LTX native dialogue request audio output'u devre disi birakiyor.");
            }

            if (request.DialogueCount > 0 && string.IsNullOrWhiteSpace(request.DialogueSourceHash))
            {
                throw new InvalidOperationException("LTX native dialogue request DialogueJson hash'i olmadan baslatilamaz.");
            }

            if (string.IsNullOrWhiteSpace(request.Prompt) ||
                !request.Prompt.Contains("speaks audibly", StringComparison.OrdinalIgnoreCase) ||
                !request.Prompt.Contains("clear Turkish pronunciation", StringComparison.OrdinalIgnoreCase) ||
                !request.Prompt.Contains("synchronized lip movement", StringComparison.OrdinalIgnoreCase) ||
                request.ExactSpokenLines.Count == 0 ||
                request.ExactSpokenLines.Any(line => !request.Prompt.Contains($"\"{line}\"", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
            }

            if (build.TimingContract?.AppliedDurationSeconds != 10)
            {
                throw new InvalidOperationException("LTX native dialogue testi 10 saniyelik klipler uretmelidir.");
            }
        }
    }

    private static object BuildJobSettingsSummary(WanGpVideoGenerationRequest request, WanGpVideoRequestBuildResult build)
    {
        return new
        {
            request.GenerationMode,
            request.ModelType,
            request.SceneId,
            request.SourceImageAssetId,
            request.Resolution,
            request.DurationSeconds,
            request.InferenceSteps,
            request.RandomSeed,
            seed = request.RandomSeed ? null : request.Seed,
            request.DialogueSourceHash,
            request.DialogueCount,
            request.SpeakerCount,
            request.CanonicalModelType,
            nativeDialogueCapabilitySupported = request.NativeDialogueCapabilitySupported,
            nativeDialogueCapabilityFailureReason = request.NativeDialogueCapabilityFailureReason,
            nativeDialogueCapabilityEvidence = request.NativeDialogueCapabilityEvidence,
            request.CharacterVoiceProfileIds,
            request.VoiceSettingsHashes,
            build.ImageInputKey,
            build.InputModeKey,
            build.InputModeValue,
            nativeAudioRequired = build.NativeAudioRequired,
            nativeAudioDisabledByRequest = build.NativeAudioDisabledByRequest,
            durationKey = build.TimingContract?.DurationKey,
            durationUnit = build.TimingContract?.DurationUnit.ToString(),
            frameCount = build.TimingContract?.CalculatedFrameCount,
            fps = build.TimingContract?.SelectedFps
        };
    }

    private static string BuildNativeDialogueMetadata(string existingMetadataJson, WanGpVideoGenerationRequest request, VideoMetadata metadata)
    {
        var json = string.IsNullOrWhiteSpace(existingMetadataJson)
            ? new JsonObject()
            : JsonNode.Parse(existingMetadataJson) as JsonObject ?? new JsonObject();
        json["GenerationMode"] = request.GenerationMode.ToString();
        json["DialogueSourceHash"] = request.DialogueSourceHash;
        json["CharacterVoiceProfileIds"] = new JsonArray(request.CharacterVoiceProfileIds.Select(id => JsonValue.Create(id)).ToArray());
        json["VoiceSettingsHashes"] = new JsonArray(request.VoiceSettingsHashes.Select(hash => JsonValue.Create(hash)).ToArray());
        json["HasNativeAudio"] = metadata.HasAudio;
        json["AudioCodec"] = metadata.AudioCodec;
        json["AudioChannels"] = metadata.AudioChannels;
        json["AudioSampleRate"] = metadata.AudioSampleRate;
        json["AudioChannelLayout"] = metadata.AudioChannelLayout;
        json["TargetDurationSeconds"] = request.DurationSeconds;
        json["ActualDurationSeconds"] = metadata.DurationSeconds;
        return json.ToJsonString(JsonOptions);
    }

    private static void ValidateImageDecodable(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException("Referans gorsel decode edilemedi.");
        }
    }

    private static void WriteOutputSummary(string externalJobId, string outputSource, string originalOutputPath, SceneMediaAsset asset)
    {
        var metadata = string.IsNullOrWhiteSpace(asset.MetadataJson)
            ? new JsonObject()
            : JsonNode.Parse(asset.MetadataJson) as JsonObject ?? new JsonObject();
        var summary = new JsonObject
        {
            ["capturedAt"] = DateTime.Now,
            ["externalJobId"] = externalJobId,
            ["outputSource"] = outputSource,
            ["artifactMediaType"] = "video",
            ["originalOutputPath"] = originalOutputPath,
            ["directorCopiedPath"] = asset.FilePath,
            ["codec"] = metadata["codec"]?.ToString() ?? metadata["Codec"]?.ToString() ?? string.Empty,
            ["duration"] = asset.DurationSeconds,
            ["assetId"] = asset.Id
        };

        var root = Path.Combine(Path.GetTempPath(), "DirectorWanGpVideoDiagnostics");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "video-output-summary.json"), summary.ToJsonString(JsonOptions));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private async Task UpdateJobAsync(int jobId, Action<GenerationJob> update, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.GenerationJobs.FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null)
        {
            return;
        }

        update(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<GenerationJob> LoadJobAsync(int jobId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == jobId, cancellationToken);
    }

    private async Task MarkJobCancelledBestEffortAsync(int jobId)
    {
        try
        {
            using var cleanup = GenerationCleanupPolicy.CreateTokenSource();
            await UpdateJobAsync(jobId, existing =>
            {
                existing.Status = GenerationJobStatus.Cancelled;
                existing.CancelRequestedAt = DateTime.Now;
                existing.CompletedAt = DateTime.Now;
                existing.UpdatedAt = DateTime.Now;
            }, cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video generation cancel cleanup failed. JobId={JobId}; TimeoutSeconds={TimeoutSeconds}", jobId, GenerationCleanupPolicy.CleanupTimeoutSeconds);
        }
    }

    private async Task MarkJobFailedBestEffortAsync(int jobId, string errorMessage)
    {
        try
        {
            using var cleanup = GenerationCleanupPolicy.CreateTokenSource();
            await UpdateJobAsync(jobId, existing =>
            {
                existing.Status = GenerationJobStatus.Failed;
                existing.ErrorMessage = errorMessage;
                existing.CompletedAt = DateTime.Now;
                existing.UpdatedAt = DateTime.Now;
            }, cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video generation failure cleanup failed. JobId={JobId}; TimeoutSeconds={TimeoutSeconds}", jobId, GenerationCleanupPolicy.CleanupTimeoutSeconds);
        }
    }

    private async Task<GenerationJob> LoadCompletedJobAsync(int jobId, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Video generation cancellation arrived after the atomic completion point. Completed DB state is preserved. JobId={JobId}", jobId);
        }

        using var cleanup = GenerationCleanupPolicy.CreateTokenSource();
        return await LoadJobAsync(jobId, cleanup.Token);
    }
}
