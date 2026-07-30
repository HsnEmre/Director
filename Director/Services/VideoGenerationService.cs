using System.IO;
using System.Text.Json;
using Director.Data;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Models;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class VideoGenerationService : IVideoGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpVideoRequestBuilder _requestBuilder;
    private readonly IWanGpVideoOutputResolver _outputResolver;
    private readonly IVideoMetadataService _metadataService;
    private readonly IMediaFileService _mediaFileService;
    private readonly IApplicationActivityCenter _activityCenter;
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
        _logger = logger;
    }

    public async Task<GenerationJob> GenerateSceneVideoAsync(
        WanGpVideoGenerationRequest request,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var scene = await LoadSceneAsync(request.SceneId, cancellationToken);
        await using var gpuLease = await _gpuCoordinator.AcquireAsync(GenerationOperationType.Video, scene.FilmProjectId, scene.Id, cancellationToken);
        GenerationJob? job = null;
        try
        {
            var reference = await LoadReferenceAssetAsync(request.SourceImageAssetId, cancellationToken);
            var beforeSnapshot = _outputResolver.CaptureSnapshot();
            job = await CreateJobAsync(scene, reference.Id, request, cancellationToken);
            _activityCenter.SetActiveJob(job.Id, null);

            var build = await _requestBuilder.BuildAsync(request, cancellationToken);
            var submission = await _wanGpClient.SubmitVideoGenerationAsync(build.Source, cancellationToken);
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

            var snapshot = await PollUntilVideoOutputAsync(job.Id, submission.ExternalJobId, scene.SceneNumber, beforeSnapshot, job.StartedAt ?? job.CreatedAt, progress, cancellationToken);
            if (snapshot.Status != GenerationJobStatus.Completed)
            {
                await UpdateJobAsync(job.Id, existing =>
                {
                    existing.Status = snapshot.Status;
                    existing.ErrorMessage = snapshot.Message;
                    existing.CompletedAt = DateTime.Now;
                    existing.UpdatedAt = DateTime.Now;
                }, CancellationToken.None);
                return await LoadJobAsync(job.Id, CancellationToken.None);
            }

            var outputPath = snapshot.GeneratedFiles.FirstOrDefault() ?? snapshot.OutputPath
                ?? throw new InvalidOperationException("WanGP video output dosyasi bulunamadi.");
            var asset = await SaveCompletedVideoAssetAsync(scene.Id, job.Id, reference.Id, outputPath, CancellationToken.None);
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
            return await LoadJobAsync(job.Id, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            if (job is not null)
            {
                await UpdateJobAsync(job.Id, existing =>
                {
                    existing.Status = GenerationJobStatus.Cancelled;
                    existing.CancelRequestedAt = DateTime.Now;
                    existing.CompletedAt = DateTime.Now;
                    existing.UpdatedAt = DateTime.Now;
                }, CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WanGP video uretimi basarisiz oldu.");
            if (job is not null)
            {
                await UpdateJobAsync(job.Id, existing =>
                {
                    existing.Status = GenerationJobStatus.Failed;
                    existing.ErrorMessage = ex.Message;
                    existing.CompletedAt = DateTime.Now;
                    existing.UpdatedAt = DateTime.Now;
                }, CancellationToken.None);
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
                }, CancellationToken.None);
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

    private async Task<SceneMediaAsset> SaveCompletedVideoAssetAsync(int sceneId, int jobId, int sourceImageAssetId, string outputPath, CancellationToken cancellationToken)
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
        var asset = await _mediaFileService.CopyGeneratedVideoAsync(scene, job, outputPath, metadata, versionNumber, true, sourceImageAssetId, reference.ThumbnailPath ?? reference.FilePath, cancellationToken);
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

    private async Task<FilmScene> LoadSceneAsync(int sceneId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes.AsNoTracking().FirstAsync(item => item.Id == sceneId, cancellationToken);
    }

    private async Task<SceneMediaAsset> LoadReferenceAssetAsync(int assetId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SceneMediaAssets.AsNoTracking().FirstAsync(item => item.Id == assetId && item.MediaType == MediaType.Image, cancellationToken);
    }

    private async Task<GenerationJob> CreateJobAsync(FilmScene scene, int sourceImageAssetId, WanGpVideoGenerationRequest request, CancellationToken cancellationToken)
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
            SettingsJson = JsonSerializer.Serialize(request, JsonOptions),
            CurrentPhase = "VideoQueued",
            PromptPreparationModel = "qwen3-vl:30b-a3b-instruct",
            PromptPreparedAt = DateTime.Now,
            CreatedAt = DateTime.Now
        };

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
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
}
