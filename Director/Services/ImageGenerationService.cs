using System.Text.Json;
using System.IO;
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

public sealed class ImageGenerationService : IImageGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IWanGpClient _wanGpClient;
    private readonly IMediaFileService _mediaFileService;
    private readonly IWanGpOutputResolver _outputResolver;
    private readonly IApplicationActivityCenter _activityCenter;
    private readonly WanGpOptions _options;
    private readonly ILogger<ImageGenerationService> _logger;
    private readonly object _activeJobLock = new();
    private string? _activeExternalJobId;

    public ImageGenerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IGpuGenerationCoordinator gpuCoordinator,
        IWanGpClient wanGpClient,
        IMediaFileService mediaFileService,
        IWanGpOutputResolver outputResolver,
        IApplicationActivityCenter activityCenter,
        IOptions<WanGpOptions> options,
        ILogger<ImageGenerationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _gpuCoordinator = gpuCoordinator;
        _wanGpClient = wanGpClient;
        _mediaFileService = mediaFileService;
        _outputResolver = outputResolver;
        _activityCenter = activityCenter;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<GenerationJob> GenerateSceneImageAsync(
        int sceneId,
        WanGpImageGenerationRequest request,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GenerationJob? job = null;
        var scene = await LoadSceneAsync(sceneId, cancellationToken);
        await using var gpuLease = await _gpuCoordinator.AcquireAsync(
            GenerationOperationType.Image,
            scene.FilmProjectId,
            scene.Id,
            cancellationToken);
        try
        {
            var beforeOutputSnapshot = _outputResolver.CaptureSnapshot();
            var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? scene.ImagePrompt : request.Prompt;
            var negativePrompt = string.IsNullOrWhiteSpace(request.NegativePrompt) ? scene.ImageNegativePrompt : request.NegativePrompt;

            job = await CreateJobAsync(scene, request, prompt, negativePrompt, cancellationToken);
            _activityCenter.SetActiveJob(job.Id, null);
            progress?.Report(new MediaGenerationProgress
            {
                Phase = "Hazırlanıyor",
                Message = $"{scene.SceneNumber}. sahne WanGP kuyruğuna hazırlanıyor.",
                OverallProgress = 2,
                SceneProgress = 2,
                CurrentSceneNumber = scene.SceneNumber,
                ModelType = request.ModelType
            });

            var schema = await _wanGpClient.GetModelSchemaAsync(request.ModelType, cancellationToken)
                ?? new WanGpModelSchema { ModelType = request.ModelType };
            var effectiveRequest = new WanGpImageGenerationRequest
            {
                ModelType = request.ModelType,
                Prompt = prompt,
                NegativePrompt = negativePrompt,
                Resolution = string.IsNullOrWhiteSpace(request.Resolution) ? "1024x1024" : request.Resolution,
                InferenceSteps = request.InferenceSteps <= 0 ? Math.Max(1, schema.DefaultInferenceSteps) : request.InferenceSteps,
                Seed = request.Seed,
                RandomSeed = request.RandomSeed,
                StopOnError = request.StopOnError
            };

            var submission = await _wanGpClient.SubmitImageGenerationAsync(effectiveRequest, schema, cancellationToken);
            if (string.IsNullOrWhiteSpace(submission.ExternalJobId))
            {
                throw new InvalidOperationException("WanGP job id döndürmedi.");
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
                existing.CurrentPhase = "WanGP çalışıyor";
                existing.UpdatedAt = DateTime.Now;
            }, cancellationToken);

            var snapshot = await PollUntilTerminalAsync(
                job.Id,
                submission.ExternalJobId,
                scene.SceneNumber,
                beforeOutputSnapshot,
                job.StartedAt ?? job.CreatedAt,
                progress,
                cancellationToken);
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

            var explicitPaths = snapshot.GeneratedFiles.ToList();
            if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
            {
                explicitPaths.Add(snapshot.OutputPath);
            }

            progress?.Report(new MediaGenerationProgress
            {
                Phase = "Output",
                Message = "WanGP output dosyasi dogrulaniyor.",
                OverallProgress = snapshot.ProgressPercentage,
                SceneProgress = snapshot.ProgressPercentage,
                CurrentSceneNumber = scene.SceneNumber,
                ModelType = request.ModelType
            });

            var outputResult = await _outputResolver.ResolveImageOutputsAsync(
                beforeOutputSnapshot,
                job.StartedAt ?? job.CreatedAt,
                explicitPaths,
                null,
                CancellationToken.None);

            if (!outputResult.Success)
            {
                await UpdateJobAsync(job.Id, existing =>
                {
                    existing.Status = GenerationJobStatus.Failed;
                    existing.ErrorMessage = outputResult.IsAmbiguous ? "OutputAmbiguous: " + outputResult.Message : outputResult.Message;
                    existing.CompletedAt = DateTime.Now;
                    existing.UpdatedAt = DateTime.Now;
                    existing.CurrentPhase = outputResult.IsAmbiguous ? "OutputAmbiguous" : "Output bulunamadi";
                }, CancellationToken.None);
                throw new InvalidOperationException(outputResult.Message);
            }

            var savedAsset = await SaveCompletedAssetAsync(scene.Id, job.Id, outputResult.Candidates, snapshot.Seed, CancellationToken.None);
            _activityCenter.AddLog("Dosya", "Gorsel Director proje klasorune kopyalandi.", GenerationLogLevel.Success);
            progress?.Report(new MediaGenerationProgress
            {
                Phase = "Tamamlandı",
                Message = $"{scene.SceneNumber}. sahne görseli kaydedildi: v{savedAsset.VersionNumber}",
                OverallProgress = 100,
                SceneProgress = 100,
                CurrentSceneNumber = scene.SceneNumber,
                ModelType = request.ModelType,
                PreviewPath = savedAsset.FilePath
            });

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
            _logger.LogError(ex, "WanGP görsel üretimi başarısız oldu.");
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

            _activityCenter.AddLog("Gorsel", "Uretim kilidi serbest birakildi. Yeni sahne uretimine hazir.", GenerationLogLevel.Information);
        }
    }

    public async Task GenerateMissingImagesAsync(
        int filmProjectId,
        WanGpImageGenerationRequest templateRequest,
        bool stopOnError,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<int> sceneIds;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            sceneIds = await db.FilmScenes
                .AsNoTracking()
                .Where(scene => scene.FilmProjectId == filmProjectId)
                .Where(scene => !db.SceneMediaAssets.Any(asset =>
                    asset.SceneId == scene.Id &&
                    asset.MediaType == MediaType.Image &&
                    asset.IsSelected))
                .OrderBy(scene => scene.SceneNumber)
                .Select(scene => scene.Id)
                .ToListAsync(cancellationToken);
        }

        for (var index = 0; index < sceneIds.Count; index++)
        {
            try
            {
                await GenerateSceneImageAsync(sceneIds[index], templateRequest, progress, cancellationToken);
            }
            catch when (!stopOnError)
            {
                progress?.Report(new MediaGenerationProgress
                {
                    Phase = "Atlandı",
                    Message = $"{index + 1}/{sceneIds.Count} üretim başarısız oldu; sonraki sahneye geçiliyor.",
                    OverallProgress = sceneIds.Count == 0 ? 0 : (index + 1) * 100d / sceneIds.Count
                });
            }
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

    public async Task SetSelectedAssetAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.SceneMediaAssets.FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken)
            ?? throw new InvalidOperationException("Medya varlığı bulunamadı.");

        var sceneAssets = await db.SceneMediaAssets
            .Where(item => item.SceneId == asset.SceneId && item.MediaType == asset.MediaType)
            .ToListAsync(cancellationToken);

        foreach (var sceneAsset in sceneAssets)
        {
            sceneAsset.IsSelected = sceneAsset.Id == assetId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SceneMediaAsset> ImportExistingWanGpOutputAsync(
        int sceneId,
        string sourcePath,
        bool makeSelected = true,
        CancellationToken cancellationToken = default)
    {
        var scene = await LoadSceneAsync(sceneId, cancellationToken);
        await using var gpuLease = await _gpuCoordinator.AcquireAsync(
            GenerationOperationType.Image,
            scene.FilmProjectId,
            scene.Id,
            cancellationToken);
        try
        {
            var job = await CreateImportJobAsync(scene, sourcePath, cancellationToken);
            var candidates = new List<WanGpOutputCandidate>
            {
                new() { FilePath = sourcePath }
            };

            return await SaveCompletedAssetAsync(scene.Id, job.Id, candidates, null, cancellationToken, forceSelected: makeSelected);
        }
        finally
        {
        }
    }

    public async Task MarkOrphanRunningJobsInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await db.GenerationJobs
            .Where(job =>
                job.Provider == GenerationProvider.WanGp &&
                (job.Status == GenerationJobStatus.Pending ||
                 job.Status == GenerationJobStatus.Queued ||
                 job.Status == GenerationJobStatus.Running))
            .ToListAsync(cancellationToken);

        foreach (var job in jobs)
        {
            job.Status = GenerationJobStatus.Interrupted;
            job.CompletedAt = DateTime.Now;
            job.UpdatedAt = DateTime.Now;
            job.ErrorMessage = "Uygulama yeniden başlatılırken açık job kesildi.";
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<FilmScene> LoadSceneAsync(int sceneId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes.AsNoTracking().FirstOrDefaultAsync(scene => scene.Id == sceneId, cancellationToken)
            ?? throw new InvalidOperationException("Sahne bulunamadı.");
    }

    private async Task<GenerationJob> CreateJobAsync(
        FilmScene scene,
        WanGpImageGenerationRequest request,
        string prompt,
        string negativePrompt,
        CancellationToken cancellationToken)
    {
        var job = new GenerationJob
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            MediaType = MediaType.Image,
            Provider = GenerationProvider.WanGp,
            Status = GenerationJobStatus.Pending,
            ModelType = request.ModelType,
            Prompt = prompt,
            NegativePrompt = negativePrompt,
            SettingsJson = JsonSerializer.Serialize(request, JsonOptions),
            CurrentPhase = "Kuyruk",
            CreatedAt = DateTime.Now
        };

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private async Task<GenerationJob> CreateImportJobAsync(FilmScene scene, string sourcePath, CancellationToken cancellationToken)
    {
        var job = new GenerationJob
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            MediaType = MediaType.Image,
            Provider = GenerationProvider.WanGp,
            Status = GenerationJobStatus.Pending,
            ModelType = "WanGP Import",
            Prompt = string.Empty,
            NegativePrompt = string.Empty,
            SettingsJson = JsonSerializer.Serialize(new { importedFrom = Path.GetFileName(sourcePath) }, JsonOptions),
            CurrentPhase = "Import",
            CreatedAt = DateTime.Now,
            StartedAt = DateTime.Now
        };

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private async Task<WanGpJobSnapshot> PollUntilTerminalAsync(
        int jobId,
        string externalJobId,
        int sceneNumber,
        WanGpOutputSnapshot beforeOutputSnapshot,
        DateTime startedAt,
        IProgress<MediaGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Max(1, _options.GenerationTimeoutMinutes)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var delay = TimeSpan.FromMilliseconds(Math.Max(250, _options.PollingIntervalMilliseconds));
        DateTime lastPersist = DateTime.MinValue;

        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            var snapshot = await _wanGpClient.GetJobAsync(externalJobId, linked.Token);
            var phase = string.IsNullOrWhiteSpace(snapshot.Phase) ? snapshot.Status.ToString() : snapshot.Phase;
            progress?.Report(new MediaGenerationProgress
            {
                Phase = phase,
                Message = $"{sceneNumber}. sahne: {snapshot.Message ?? phase}",
                OverallProgress = snapshot.ProgressPercentage,
                SceneProgress = snapshot.ProgressPercentage,
                CurrentStep = snapshot.CurrentStep,
                TotalSteps = snapshot.TotalSteps,
                CurrentSceneNumber = sceneNumber,
                ExternalJobId = externalJobId,
                PreviewPath = snapshot.OutputPath
            });

            if ((DateTime.Now - lastPersist).TotalSeconds >= 2 || IsTerminal(snapshot.Status))
            {
                await UpdateJobAsync(jobId, job =>
                {
                    job.Status = snapshot.Status;
                    job.ProgressPercentage = snapshot.ProgressPercentage;
                    job.CurrentPhase = phase;
                    job.CurrentStep = snapshot.CurrentStep;
                    job.TotalSteps = snapshot.TotalSteps;
                    job.UpdatedAt = DateTime.Now;
                }, CancellationToken.None);
                lastPersist = DateTime.Now;
            }

            if (IsTerminal(snapshot.Status))
            {
                return snapshot;
            }

            var explicitPaths = snapshot.GeneratedFiles.ToList();
            if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
            {
                explicitPaths.Add(snapshot.OutputPath);
            }

            var outputResult = await _outputResolver.ResolveImageOutputsAsync(
                beforeOutputSnapshot,
                startedAt,
                explicitPaths,
                TimeSpan.FromSeconds(1),
                linked.Token);
            if (outputResult.Success)
            {
                var paths = outputResult.Candidates
                    .Select(candidate => candidate.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                progress?.Report(new MediaGenerationProgress
                {
                    Phase = "Output",
                    Message = $"{sceneNumber}. sahne output dosyasi bulundu; MCP terminal durumu beklenmeden yerel tamamlama yapiliyor.",
                    OverallProgress = Math.Max(snapshot.ProgressPercentage, 95),
                    SceneProgress = Math.Max(snapshot.ProgressPercentage, 95),
                    CurrentSceneNumber = sceneNumber,
                    ExternalJobId = externalJobId,
                    PreviewPath = paths.FirstOrDefault()
                });

                await UpdateJobAsync(jobId, job =>
                {
                    job.Status = GenerationJobStatus.Completed;
                    job.ProgressPercentage = Math.Max(job.ProgressPercentage, 95);
                    job.CurrentPhase = "Output resolved";
                    job.UpdatedAt = DateTime.Now;
                }, CancellationToken.None);

                snapshot.Status = GenerationJobStatus.Completed;
                snapshot.Message = "OutputResolvedBeforeMcpTerminalState";
                snapshot.OutputPath = paths.FirstOrDefault();
                snapshot.GeneratedFiles = paths;
                return snapshot;
            }

            if (outputResult.IsAmbiguous)
            {
                snapshot.Status = GenerationJobStatus.Failed;
                snapshot.Message = outputResult.Message;
                return snapshot;
            }

            await Task.Delay(delay, linked.Token);
        }
    }

    private async Task<SceneMediaAsset> SaveCompletedAssetAsync(
        int sceneId,
        int jobId,
        IReadOnlyList<WanGpOutputCandidate> outputs,
        int? seed,
        CancellationToken cancellationToken,
        bool? forceSelected = null)
    {
        FilmScene scene;
        GenerationJob job;
        int versionNumber;
        bool isSelected;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            scene = await db.FilmScenes.AsNoTracking().FirstAsync(item => item.Id == sceneId, cancellationToken);
            job = await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == jobId, cancellationToken);
            var existingAssets = await db.SceneMediaAssets
                .Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Image)
                .ToListAsync(cancellationToken);
            versionNumber = existingAssets.Count == 0 ? 1 : existingAssets.Max(item => item.VersionNumber) + 1;
            isSelected = forceSelected ?? existingAssets.All(item => !item.IsSelected);
        }

        var firstOutput = outputs.FirstOrDefault()
            ?? throw new InvalidOperationException("WanGP output dosyasi bulunamadi.");
        var asset = await _mediaFileService.CopyImageAsync(scene, job, firstOutput.FilePath, versionNumber, isSelected, seed, cancellationToken);

        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (asset.IsSelected)
            {
                var previous = await db.SceneMediaAssets
                    .Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Image)
                    .ToListAsync(cancellationToken);
                foreach (var item in previous)
                {
                    item.IsSelected = false;
                }
            }

            db.SceneMediaAssets.Add(asset);
            var trackedJob = await db.GenerationJobs.FirstAsync(item => item.Id == jobId, cancellationToken);
            trackedJob.Status = GenerationJobStatus.Completed;
            trackedJob.ProgressPercentage = 100;
            trackedJob.CurrentPhase = "Tamamlandı";
            trackedJob.CompletedAt = DateTime.Now;
            trackedJob.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return asset;
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

    private static bool IsTerminal(GenerationJobStatus status)
    {
        return status is GenerationJobStatus.Completed or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled or GenerationJobStatus.Interrupted;
    }
}
