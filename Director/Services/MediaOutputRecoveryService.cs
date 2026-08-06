using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Director.Data;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class MediaOutputRecoveryService : IMediaOutputRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IWanGpFinalOutputResolver _resolver;
    private readonly IVideoMetadataService _metadataService;
    private readonly IMediaFileService _mediaFileService;
    private readonly IMediaOutputRecoveryLeaseCoordinator _leaseCoordinator;
    private readonly WanGpOptions _options;
    private readonly ILogger<MediaOutputRecoveryService> _logger;

    public MediaOutputRecoveryService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IWanGpFinalOutputResolver resolver,
        IVideoMetadataService metadataService,
        IMediaFileService mediaFileService,
        IMediaOutputRecoveryLeaseCoordinator leaseCoordinator,
        IOptions<WanGpOptions> options,
        ILogger<MediaOutputRecoveryService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _resolver = resolver;
        _metadataService = metadataService;
        _mediaFileService = mediaFileService;
        _leaseCoordinator = leaseCoordinator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<MediaOutputRecoveryPlan> PlanVideoRecoveryAsync(
        MediaOutputRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var job = await LoadJobSnapshotAsync(request, cancellationToken);
        return await BuildPlanAsync(job, request.Seed, TimeSpan.FromSeconds(2), cancellationToken);
    }

    public async Task<MediaOutputRecoveryWriteResult> WriteVideoRecoveryAsync(
        MediaOutputRecoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.GenerationJobId is not int jobId || jobId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.GenerationJobId), "--write icin pozitif --job-id zorunlu.");
        }

        await using var lease = await _leaseCoordinator.AcquireAsync(jobId, cancellationToken);
        var job = await LoadJobSnapshotAsync(request, cancellationToken);
        var plan = await BuildPlanAsync(job, request.Seed, TimeSpan.FromSeconds(5), cancellationToken);
        if (plan.ExistingAssetForJob)
        {
            return new MediaOutputRecoveryWriteResult
            {
                RecoverySucceeded = true,
                AlreadyRecovered = true,
                GenerationJobId = plan.GenerationJobId,
                SceneId = plan.SceneId,
                SceneNumber = plan.SceneNumber,
                SourcePreserved = !string.IsNullOrWhiteSpace(plan.ResolvedFinalPath) && File.Exists(plan.ResolvedFinalPath),
                SourceFileSize = plan.FinalSize,
                DestinationExists = true,
                ExistingVideoAssetCount = plan.ExistingVideoAssetCount,
                WanGpSubmitCount = 0,
                OllamaCallCount = 0,
                DbWriteCount = 0,
                FileCopyCount = 0,
                JobStatus = job.GenerationJob.Status.ToString(),
                JobCurrentPhase = job.GenerationJob.CurrentPhase,
                Message = "AlreadyRecovered"
            };
        }

        if (plan.Ambiguous)
        {
            throw new WanGpAmbiguousOutputException(plan.Message);
        }

        if (!plan.RecoveryPossible || string.IsNullOrWhiteSpace(plan.ResolvedFinalPath))
        {
            throw new MediaOutputRecoveryNotPossibleException(plan.Message);
        }

        if (WanGpFinalOutputResolver.IsTransientPath(plan.ResolvedFinalPath))
        {
            throw new MediaOutputRecoveryNotPossibleException("Transient output final source olarak kullanilamaz.");
        }

        var sourcePath = Path.GetFullPath(plan.ResolvedFinalPath);
        var sourceFingerprint = await HashFileAsync(sourcePath, cancellationToken);
        var metadata = await _metadataService.ProbeAsync(sourcePath, cancellationToken);
        if (!metadata.HasVideo || metadata.DurationSeconds is null or <= 0)
        {
            throw new MediaOutputRecoveryImportException("Source video ffprobe dogrulamasindan gecmedi.");
        }

        if (IsNativeDialogueJob(job) && !metadata.HasAudio)
        {
            throw new MediaOutputRecoveryImportException("Native-dialogue video recovery icin audio stream zorunlu.");
        }

        SceneMediaAsset? importedAsset = null;
        try
        {
            var isSelected = job.ExistingVideoAssetCount == 0;
            importedAsset = await _mediaFileService.CopyGeneratedVideoAsync(
                job.Scene,
                job.GenerationJob,
                sourcePath,
                metadata,
                job.NextVersionNumber,
                isSelected,
                job.GenerationJob.SourceMediaAssetId ?? 0,
                job.SourceThumbnailPath ?? job.SourceImagePath,
                cancellationToken);
            importedAsset.Seed = plan.Seed;
            importedAsset.Role = IsNativeDialogueJob(job)
                ? MediaAssetRole.GeneratedNativeDialogueVideo
                : MediaAssetRole.GeneratedSilentVideo;
            importedAsset.MetadataJson = BuildRecoveryMetadata(importedAsset.MetadataJson, sourcePath, sourceFingerprint, metadata);

            var destinationFingerprint = await HashFileAsync(importedAsset.FilePath, cancellationToken);
            if (!string.Equals(sourceFingerprint, destinationFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(importedAsset.FilePath);
                throw new MediaOutputRecoveryImportException("Destination fingerprint source ile eslesmedi.");
            }

            var completion = await CompleteDbAsync(jobId, importedAsset, cancellationToken);
            if (completion.AlreadyRecovered)
            {
                TryDelete(importedAsset.FilePath);
                return new MediaOutputRecoveryWriteResult
                {
                    RecoverySucceeded = true,
                    AlreadyRecovered = true,
                    GenerationJobId = jobId,
                    SceneId = job.Scene.Id,
                    SceneNumber = job.Scene.SceneNumber,
                    SourcePreserved = File.Exists(sourcePath),
                    SourceFileSize = new FileInfo(sourcePath).Length,
                    ExistingVideoAssetCount = completion.ExistingVideoAssetCount,
                    WanGpSubmitCount = 0,
                    OllamaCallCount = 0,
                    DbWriteCount = 0,
                    FileCopyCount = 0,
                    JobStatus = completion.JobStatus,
                    JobCurrentPhase = completion.JobCurrentPhase,
                    Message = "AlreadyRecovered"
                };
            }

            return new MediaOutputRecoveryWriteResult
            {
                RecoverySucceeded = true,
                AlreadyRecovered = false,
                GenerationJobId = jobId,
                SceneId = job.Scene.Id,
                SceneNumber = job.Scene.SceneNumber,
                SourcePreserved = File.Exists(sourcePath),
                SourceFileSize = new FileInfo(sourcePath).Length,
                DestinationFileName = Path.GetFileName(importedAsset.FilePath),
                DestinationExists = File.Exists(importedAsset.FilePath),
                DestinationFileSize = new FileInfo(importedAsset.FilePath).Length,
                FingerprintMatch = true,
                AssetCreated = true,
                SceneMediaAssetId = completion.SceneMediaAssetId,
                VersionNumber = importedAsset.VersionNumber,
                IsSelected = importedAsset.IsSelected,
                JobStatus = completion.JobStatus,
                JobCurrentPhase = completion.JobCurrentPhase,
                ExistingVideoAssetCount = completion.ExistingVideoAssetCount,
                WanGpSubmitCount = 0,
                OllamaCallCount = 0,
                DbWriteCount = 1,
                FileCopyCount = 1,
                Message = "RecoverySucceeded"
            };
        }
        catch (MediaOutputRecoveryImportException)
        {
            throw;
        }
        catch (MediaOutputRecoveryDbException)
        {
            if (importedAsset is not null)
            {
                TryDelete(importedAsset.FilePath);
            }

            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (importedAsset is not null)
            {
                TryDelete(importedAsset.FilePath);
            }

            throw new MediaOutputRecoveryImportException("Recovery import/file validation basarisiz.", ex);
        }
    }

    private async Task<RecoveryJobSnapshot> LoadJobSnapshotAsync(MediaOutputRecoveryRequest request, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.GenerationJobs
            .Include(job => job.Scene)
            .AsNoTracking()
            .Where(job => job.MediaType == MediaType.Video);
        if (request.GenerationJobId is int jobId)
        {
            query = query.Where(job => job.Id == jobId);
        }
        else if (request.FilmProjectId is int filmProjectId && request.SceneId is int sceneId)
        {
            query = query
                .Where(job => job.FilmProjectId == filmProjectId && job.SceneId == sceneId)
                .OrderByDescending(job => job.Id);
        }
        else
        {
            throw new InvalidOperationException("--job-id veya --film-project-id + --scene-id gerekli.");
        }

        var job = await query.FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Recovery icin video job bulunamadi.");
        var existingVideoAssets = await db.SceneMediaAssets.AsNoTracking()
            .Where(asset => asset.FilmProjectId == job.FilmProjectId && asset.SceneId == job.SceneId && asset.MediaType == MediaType.Video)
            .ToListAsync(cancellationToken);
        var existingAssetForJob = existingVideoAssets.FirstOrDefault(asset => asset.GenerationJobId == job.Id);
        var sourceAsset = job.SourceMediaAssetId is int sourceId
            ? await db.SceneMediaAssets.AsNoTracking().FirstOrDefaultAsync(asset => asset.Id == sourceId, cancellationToken)
            : null;

        return new RecoveryJobSnapshot(
            job,
            job.Scene,
            existingVideoAssets.Count,
            existingVideoAssets.Count == 0 ? 1 : existingVideoAssets.Max(asset => asset.VersionNumber) + 1,
            existingAssetForJob,
            sourceAsset?.FilePath,
            sourceAsset?.ThumbnailPath);
    }

    private async Task<MediaOutputRecoveryPlan> BuildPlanAsync(
        RecoveryJobSnapshot job,
        int? requestedSeed,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        var seed = requestedSeed ?? TryReadSeed(job.GenerationJob.ErrorMessage) ?? TryReadSeed(job.GenerationJob.SettingsJson) ?? TryReadSeed(job.GenerationJob.Prompt);
        var transientPath = TryReadPath(job.GenerationJob.ErrorMessage);
        var plan = new MediaOutputRecoveryPlan
        {
            GenerationJobId = job.GenerationJob.Id,
            FilmProjectId = job.GenerationJob.FilmProjectId,
            SceneId = job.GenerationJob.SceneId,
            SceneNumber = job.Scene.SceneNumber,
            JobStatus = job.GenerationJob.Status.ToString(),
            CurrentPhase = job.GenerationJob.CurrentPhase,
            Seed = seed,
            ExpectedOrTransientPath = transientPath,
            TransientExists = !string.IsNullOrWhiteSpace(transientPath) && File.Exists(transientPath),
            ExistingVideoAssetCount = job.ExistingVideoAssetCount,
            ExistingAssetForJob = job.ExistingAssetForJob is not null,
            IntendedDestination = BuildIntendedDestination(job.GenerationJob.FilmProjectId, job.Scene.SceneNumber)
        };

        if (job.ExistingAssetForJob is not null)
        {
            plan.ResolvedFinalPath = job.ExistingAssetForJob.FilePath;
            plan.FinalExists = File.Exists(job.ExistingAssetForJob.FilePath);
            plan.FinalSize = plan.FinalExists ? new FileInfo(job.ExistingAssetForJob.FilePath).Length : null;
            plan.RecoveryPossible = false;
            plan.Message = "Bu job icin zaten video asset var; duplicate import yapilmaz.";
            return plan;
        }

        try
        {
            var explicitPaths = string.IsNullOrWhiteSpace(transientPath) ? Array.Empty<string>() : new[] { transientPath };
            var resolution = await _resolver.ResolveAsync(new WanGpFinalOutputResolveRequest
            {
                MediaKind = WanGpOutputMediaKind.Video,
                BeforeSnapshot = new WanGpOutputSnapshot(),
                StartedAt = job.GenerationJob.StartedAt ?? job.GenerationJob.CreatedAt,
                CompletedAt = job.GenerationJob.CompletedAt,
                ExplicitPaths = explicitPaths,
                ExternalJobId = job.GenerationJob.ExternalJobId,
                JobId = job.GenerationJob.Id,
                SceneId = job.GenerationJob.SceneId,
                Seed = seed,
                RequireAudio = IsNativeDialogueJob(job),
                MaxWait = maxWait
            }, cancellationToken);

            var candidate = resolution.Candidate;
            var metadata = await _metadataService.ProbeAsync(candidate.FilePath, cancellationToken);
            plan.ResolvedFinalPath = candidate.FilePath;
            plan.FinalExists = File.Exists(candidate.FilePath);
            plan.FinalSize = candidate.FileSize;
            plan.DurationSeconds = metadata.DurationSeconds;
            plan.HasVideo = metadata.HasVideo;
            plan.HasAudio = metadata.HasAudio;
            plan.Evidence = candidate.Evidence;
            var seedSatisfied = seed is null || candidate.Evidence.Contains("SeedMatch", StringComparer.OrdinalIgnoreCase);
            plan.RecoveryPossible = plan.FinalExists &&
                metadata.HasVideo &&
                metadata.DurationSeconds is > 0 &&
                seedSatisfied &&
                (!IsNativeDialogueJob(job) || metadata.HasAudio);
            plan.Message = plan.RecoveryPossible
                ? "Recovery dry-run basarili; --write ile import edilebilir."
                : "Recovery icin uygun final output bulunamadi.";
        }
        catch (WanGpAmbiguousOutputException ex)
        {
            plan.Ambiguous = true;
            plan.Message = ex.Message;
        }
        catch (WanGpOutputFinalizationTimeoutException ex)
        {
            plan.Message = ex.Message;
        }

        return plan;
    }

    private async Task<DbCompletionResult> CompleteDbAsync(
        int jobId,
        SceneMediaAsset importedAsset,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var job = await db.GenerationJobs
                .Include(item => item.Scene)
                .FirstAsync(item => item.Id == jobId && item.MediaType == MediaType.Video, cancellationToken);
            var existingForJob = await db.SceneMediaAssets
                .FirstOrDefaultAsync(asset => asset.GenerationJobId == jobId && asset.MediaType == MediaType.Video, cancellationToken);
            var existingVideoAssets = await db.SceneMediaAssets
                .Where(asset => asset.SceneId == job.SceneId && asset.MediaType == MediaType.Video)
                .ToListAsync(cancellationToken);
            if (existingForJob is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new DbCompletionResult(true, existingForJob.Id, existingVideoAssets.Count, existingForJob.VersionNumber, existingForJob.IsSelected, job.Status.ToString(), job.CurrentPhase);
            }

            importedAsset.FilmProjectId = job.FilmProjectId;
            importedAsset.SceneId = job.SceneId;
            importedAsset.GenerationJobId = job.Id;
            importedAsset.SourceMediaAssetId = job.SourceMediaAssetId;
            importedAsset.ModelType = job.ModelType;
            importedAsset.VersionNumber = existingVideoAssets.Count == 0 ? 1 : existingVideoAssets.Max(asset => asset.VersionNumber) + 1;
            importedAsset.IsSelected = existingVideoAssets.Count == 0 || importedAsset.IsSelected;
            if (importedAsset.IsSelected)
            {
                foreach (var asset in existingVideoAssets)
                {
                    asset.IsSelected = false;
                }
            }

            db.SceneMediaAssets.Add(importedAsset);
            job.Status = GenerationJobStatus.Completed;
            job.ProgressPercentage = 100;
            job.CurrentPhase = "Completed";
            job.ErrorMessage = null;
            job.CompletedAt = DateTime.Now;
            job.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new DbCompletionResult(false, importedAsset.Id, existingVideoAssets.Count + 1, importedAsset.VersionNumber, importedAsset.IsSelected, job.Status.ToString(), job.CurrentPhase);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or IOException)
        {
            _logger.LogError(ex, "Media output recovery DB transaction failed. JobId={JobId}", jobId);
            throw new MediaOutputRecoveryDbException("Recovery DB transaction basarisiz.", ex);
        }
    }

    private string BuildIntendedDestination(int filmProjectId, int sceneNumber)
    {
        var root = Path.GetFullPath(_options.GetEffectiveOutputRootPath());
        return Path.Combine(root, filmProjectId.ToString(), "scenes", sceneNumber.ToString("000"), "videos", $"scene-{sceneNumber:000}-video-vNEXT-{{guid}}.mp4");
    }

    private static string BuildRecoveryMetadata(string existingMetadataJson, string sourcePath, string sourceFingerprint, VideoMetadata metadata)
    {
        var json = string.IsNullOrWhiteSpace(existingMetadataJson)
            ? new JsonObject()
            : JsonNode.Parse(existingMetadataJson) as JsonObject ?? new JsonObject();
        json["Recovery"] = new JsonObject
        {
            ["RecoveredAt"] = DateTime.Now,
            ["SourceFileName"] = Path.GetFileName(sourcePath),
            ["SourceSha256"] = sourceFingerprint,
            ["HasVideo"] = metadata.HasVideo,
            ["HasAudio"] = metadata.HasAudio,
            ["DurationSeconds"] = metadata.DurationSeconds
        };
        return json.ToJsonString(JsonOptions);
    }

    private static bool IsNativeDialogueJob(RecoveryJobSnapshot job) =>
        job.GenerationJob.SettingsJson.Contains("LtxNativeDialogue", StringComparison.OrdinalIgnoreCase) ||
        job.GenerationJob.SettingsJson.Contains("\"generationMode\": 2", StringComparison.OrdinalIgnoreCase) ||
        job.GenerationJob.SettingsJson.Contains("\"generationMode\":2", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort orphan cleanup; retry remains idempotent through GenerationJobId checks.
        }
    }

    private static int? TryReadSeed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, "seed(?<seed>\\d+)|\"seed\"\\s*:\\s*(?<jsonSeed>\\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Groups["seed"].Success ? match.Groups["seed"].Value : match.Groups["jsonSeed"].Value;
        return int.TryParse(value, out var seed) ? seed : null;
    }

    private static string? TryReadPath(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Regex.Match(text, "'(?<path>[A-Z]:\\\\[^']+)'");
        return match.Success ? match.Groups["path"].Value : null;
    }

    private sealed record RecoveryJobSnapshot(
        GenerationJob GenerationJob,
        FilmScene Scene,
        int ExistingVideoAssetCount,
        int NextVersionNumber,
        SceneMediaAsset? ExistingAssetForJob,
        string? SourceImagePath,
        string? SourceThumbnailPath);

    private sealed record DbCompletionResult(
        bool AlreadyRecovered,
        int SceneMediaAssetId,
        int ExistingVideoAssetCount,
        int VersionNumber,
        bool IsSelected,
        string JobStatus,
        string JobCurrentPhase);
}
