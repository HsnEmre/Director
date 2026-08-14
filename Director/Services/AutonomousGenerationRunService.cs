using System.IO;
using System.Text.Json;
using Director.Data;
using Director.Dtos.Autonomous;
using Director.Enums;
using Director.Models;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class AutonomousGenerationRunService : IAutonomousGenerationRunService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly AutonomousGenerationRunStatus[] ActiveStatuses =
    [
        AutonomousGenerationRunStatus.Pending,
        AutonomousGenerationRunStatus.Validating,
        AutonomousGenerationRunStatus.GeneratingStoryNarrative,
        AutonomousGenerationRunStatus.GeneratingCharacters,
        AutonomousGenerationRunStatus.GeneratingNarrativeScenes,
        AutonomousGenerationRunStatus.GeneratingImagePrompts,
        AutonomousGenerationRunStatus.GeneratingStory,
        AutonomousGenerationRunStatus.GeneratingScenes,
        AutonomousGenerationRunStatus.GeneratingImages,
        AutonomousGenerationRunStatus.GeneratingVideoPrompts,
        AutonomousGenerationRunStatus.GeneratingVideos,
        AutonomousGenerationRunStatus.GeneratingAudio,
        AutonomousGenerationRunStatus.Finalizing,
        AutonomousGenerationRunStatus.CancelRequested,
        AutonomousGenerationRunStatus.Paused
    ];

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IAutonomousGenerationStateMachine _stateMachine;
    private readonly IVideoModelCapabilityService _videoModelCapabilityService;
    private readonly ILogger<AutonomousGenerationRunService> _logger;

    public AutonomousGenerationRunService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IAutonomousGenerationStateMachine stateMachine,
        IVideoModelCapabilityService videoModelCapabilityService,
        ILogger<AutonomousGenerationRunService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _stateMachine = stateMachine;
        _videoModelCapabilityService = videoModelCapabilityService;
        _logger = logger;
    }

    public async Task<AutonomousGenerationRunSummary> StartOrGetActiveRunAsync(
        int filmProjectId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await dbContext.AutonomousGenerationRuns
            .AsNoTracking()
            .Where(run => run.FilmProjectId == filmProjectId && ActiveStatuses.Contains(run.Status))
            .OrderByDescending(run => run.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return ToSummary(existing);
        }

        snapshot.FilmProjectId = filmProjectId;
        EnsureSnapshotVideoDurationCompatible(snapshot);
        snapshot.CreatedAtUtc = DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var run = new AutonomousGenerationRun
        {
            FilmProjectId = filmProjectId,
            Status = AutonomousGenerationRunStatus.Pending,
            CurrentStage = AutonomousGenerationStage.Pending,
            StartedAtUtc = now,
            UpdatedAtUtc = now,
            LastHeartbeatAtUtc = now,
            ConfigurationSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions),
            CorrelationId = Guid.NewGuid().ToString("N"),
            LastMessage = "Otonom üretim kuyruğa alındı."
        };

        dbContext.AutonomousGenerationRuns.Add(run);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return ToSummary(run);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Autonomous run insert conflicted for project {ProjectId}; returning existing active run.", filmProjectId);
            var conflictedExisting = await dbContext.AutonomousGenerationRuns
                .AsNoTracking()
                .Where(item => item.FilmProjectId == filmProjectId && ActiveStatuses.Contains(item.Status))
                .OrderByDescending(item => item.StartedAtUtc)
                .FirstAsync(cancellationToken);
            return ToSummary(conflictedExisting);
        }
    }

    public async Task<AutonomousGenerationRun?> GetRunAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.AutonomousGenerationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(run => run.Id == runId, cancellationToken);
    }

    public async Task<AutonomousGenerationRunSummary?> GetLatestRunForProjectAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.AutonomousGenerationRuns
            .AsNoTracking()
            .Where(item => item.FilmProjectId == filmProjectId)
            .OrderByDescending(item => item.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return run is null ? null : ToSummary(run);
    }

    public async Task<AutonomousProjectCheckpoint> GetProjectCheckpointAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await dbContext.FilmProjects
            .AsNoTracking()
            .Include(item => item.Story)
                .ThenInclude(story => story!.Characters)
            .FirstOrDefaultAsync(item => item.Id == filmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadı.");

        var scenes = await dbContext.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == filmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .ToListAsync(cancellationToken);

        var sceneIds = scenes.Select(scene => scene.Id).ToHashSet();
        var assets = await dbContext.SceneMediaAssets
            .AsNoTracking()
            .Where(asset => asset.FilmProjectId == filmProjectId && sceneIds.Contains(asset.SceneId))
            .OrderByDescending(asset => asset.IsSelected)
            .ThenByDescending(asset => asset.CreatedAt)
            .ToListAsync(cancellationToken);

        var expectedSceneCount = Math.Max(project.CalculatedClipCount, scenes.Count);
        var sceneNumbers = scenes.Select(scene => scene.SceneNumber).ToHashSet();
        var firstMissingNarrativeScene = Enumerable.Range(1, expectedSceneCount)
            .FirstOrDefault(sceneNumber => !sceneNumbers.Contains(sceneNumber));

        return new AutonomousProjectCheckpoint
        {
            FilmProjectId = filmProjectId,
            ExpectedSceneCount = expectedSceneCount,
            SceneCount = scenes.Count,
            HasValidStory = HasValidStory(project.Story),
            HasValidCharacters = HasValidCharacters(project.Story),
            FirstMissingNarrativeSceneNumber = firstMissingNarrativeScene > 0 ? firstMissingNarrativeScene : null,
            FirstMissingImagePromptSceneNumber = scenes
                .Where(scene => string.IsNullOrWhiteSpace(scene.ImagePrompt) || string.IsNullOrWhiteSpace(scene.ImageNegativePrompt))
                .Select(scene => (int?)scene.SceneNumber)
                .FirstOrDefault(),
            FirstMissingVideoPromptSceneNumber = scenes
                .Where(scene =>
                    string.IsNullOrWhiteSpace(scene.VideoPrompt) ||
                    string.IsNullOrWhiteSpace(scene.VideoNegativePrompt) ||
                    StoryGenerationService.HasInvalidSilentVideoPromptFields(scene.VideoPrompt, scene.VideoNegativePrompt))
                .Select(scene => (int?)scene.SceneNumber)
                .FirstOrDefault(),
            FirstMissingSelectedImageSceneNumber = scenes
                .Where(scene => FindValidAsset(assets, scene.Id, MediaType.Image, null, selectedOnly: true) is null)
                .Select(scene => (int?)scene.SceneNumber)
                .FirstOrDefault(),
            FirstMissingSelectedVideoSceneNumber = scenes
                .Where(scene => FindValidAsset(assets, scene.Id, MediaType.Video, null, selectedOnly: true) is null)
                .Select(scene => (int?)scene.SceneNumber)
                .FirstOrDefault(),
            FirstMissingSceneAudioSceneNumber = scenes
                .Where(scene => FindValidAsset(assets, scene.Id, MediaType.Audio, MediaAssetRole.SceneSpeechTrack, selectedOnly: false) is null)
                .Select(scene => (int?)scene.SceneNumber)
                .FirstOrDefault()
        };
    }

    public async Task<IReadOnlyList<AutonomousGenerationRunSummary>> GetRunnableRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runs = await dbContext.AutonomousGenerationRuns
            .AsNoTracking()
            .Where(run => ActiveStatuses.Contains(run.Status) && run.Status != AutonomousGenerationRunStatus.Paused)
            .OrderBy(run => run.StartedAtUtc)
            .ToListAsync(cancellationToken);

        return runs.Select(ToSummary).ToList();
    }

    public async Task<bool> TryClaimRunAsync(
        int runId,
        string workerId,
        TimeSpan staleHeartbeatThreshold,
        TimeSpan leaseExtension,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            throw new ArgumentException("Worker id is required.", nameof(workerId));
        }

        var now = DateTime.UtcNow;
        var staleBeforeUtc = now - staleHeartbeatThreshold;
        var leaseExpiresAtUtc = now + leaseExtension;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var affectedRows = await dbContext.AutonomousGenerationRuns
            .Where(run =>
                run.Id == runId &&
                ActiveStatuses.Contains(run.Status) &&
                run.Status != AutonomousGenerationRunStatus.Paused &&
                (run.WorkerId == null ||
                 run.WorkerId == workerId ||
                 run.LastHeartbeatAtUtc < staleBeforeUtc))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.WorkerId, workerId)
                .SetProperty(run => run.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                .SetProperty(run => run.LastHeartbeatAtUtc, now)
                .SetProperty(run => run.UpdatedAtUtc, now)
                .SetProperty(run => run.LastMessage, "Otonom run worker tarafından sahiplenildi."),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task<bool> TryRenewLeaseAsync(
        int runId,
        string workerId,
        TimeSpan leaseExtension,
        string message,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var leaseExpiresAtUtc = now + leaseExtension;
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var affectedRows = await dbContext.AutonomousGenerationRuns
            .Where(run =>
                run.Id == runId &&
                run.WorkerId == workerId &&
                ActiveStatuses.Contains(run.Status) &&
                run.Status != AutonomousGenerationRunStatus.Paused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                .SetProperty(run => run.LastHeartbeatAtUtc, now)
                .SetProperty(run => run.UpdatedAtUtc, now)
                .SetProperty(run => run.LastMessage, message),
                cancellationToken);

        return affectedRows == 1;
    }

    public async Task<bool> IsRunOwnedByWorkerAsync(int runId, string workerId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.AutonomousGenerationRuns
            .AsNoTracking()
            .AnyAsync(run => run.Id == runId && run.WorkerId == workerId, cancellationToken);
    }

    public async Task ReleaseClaimAsync(int runId, string workerId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.AutonomousGenerationRuns
            .Where(run => run.Id == runId && run.WorkerId == workerId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(run => run.WorkerId, (string?)null)
                .SetProperty(run => run.LeaseExpiresAtUtc, (DateTime?)null),
                cancellationToken);
    }

    public async Task<FilmProject> GetProjectAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.FilmProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(project => project.Id == filmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadı.");
    }

    public async Task<IReadOnlyList<FilmScene>> GetScenesAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == filmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AutonomousSceneWorkItem>> EnsureSceneWorkItemsAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.AutonomousGenerationRuns
            .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken)
            ?? throw new InvalidOperationException("Otonom çalışma bulunamadı.");

        var scenes = await dbContext.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == run.FilmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .ToListAsync(cancellationToken);

        var existing = await dbContext.AutonomousSceneWorkItems
            .Where(item => item.AutonomousGenerationRunId == runId)
            .ToListAsync(cancellationToken);
        var existingSceneIds = existing.Select(item => item.StorySceneId).ToHashSet();
        var now = DateTime.UtcNow;
        foreach (var scene in scenes.Where(scene => !existingSceneIds.Contains(scene.Id)))
        {
            dbContext.AutonomousSceneWorkItems.Add(new AutonomousSceneWorkItem
            {
                AutonomousGenerationRunId = runId,
                StorySceneId = scene.Id,
                SceneNumber = scene.SceneNumber,
                ImageStatus = AutonomousWorkItemStatus.Pending,
                VideoStatus = AutonomousWorkItemStatus.Pending,
                AudioStatus = AutonomousWorkItemStatus.Pending,
                FinalizationStatus = AutonomousWorkItemStatus.Pending,
                UpdatedAtUtc = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        existing = await dbContext.AutonomousSceneWorkItems
            .Where(item => item.AutonomousGenerationRunId == runId)
            .ToListAsync(cancellationToken);

        var sceneIds = scenes.Select(scene => scene.Id).ToHashSet();
        var assets = await dbContext.SceneMediaAssets
            .AsNoTracking()
            .Where(asset => asset.FilmProjectId == run.FilmProjectId && sceneIds.Contains(asset.SceneId))
            .OrderByDescending(asset => asset.IsSelected)
            .ThenByDescending(asset => asset.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var item in existing)
        {
            var selectedImage = FindValidAsset(assets, item.StorySceneId, MediaType.Image, null, selectedOnly: true);
            if (selectedImage is not null)
            {
                item.ImageStatus = AutonomousWorkItemStatus.Completed;
                item.ImageMediaAssetId = selectedImage.Id;
                item.LastError = string.Empty;
                item.UpdatedAtUtc = now;
            }

            var selectedVideo = FindValidAsset(assets, item.StorySceneId, MediaType.Video, null, selectedOnly: true);
            if (selectedVideo is not null)
            {
                item.VideoStatus = AutonomousWorkItemStatus.Completed;
                item.VideoMediaAssetId = selectedVideo.Id;
                item.LastError = string.Empty;
                item.UpdatedAtUtc = now;
            }

            var sceneAudio = FindValidAsset(assets, item.StorySceneId, MediaType.Audio, MediaAssetRole.SceneSpeechTrack, selectedOnly: false);
            if (sceneAudio is not null)
            {
                item.AudioStatus = AutonomousWorkItemStatus.Completed;
                item.AudioMediaAssetId = sceneAudio.Id;
                item.LastError = string.Empty;
                item.UpdatedAtUtc = now;
            }
        }

        run.TotalSceneCount = scenes.Count;
        run.CompletedSceneCount = existing.Count(IsSceneWorkItemCompleted);
        run.UpdatedAtUtc = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        return await dbContext.AutonomousSceneWorkItems
            .AsNoTracking()
            .Where(item => item.AutonomousGenerationRunId == runId)
            .OrderBy(item => item.SceneNumber)
            .ToListAsync(cancellationToken);
    }

    public Task<SceneMediaAsset?> FindValidSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
        FindValidAssetAsync(sceneId, MediaType.Image, null, selectedOnly: true, cancellationToken);

    public Task<SceneMediaAsset?> FindValidSelectedVideoAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
        FindValidAssetAsync(sceneId, MediaType.Video, null, selectedOnly: true, cancellationToken);

    public Task<SceneMediaAsset?> FindValidSceneAudioAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
        FindValidAssetAsync(sceneId, MediaType.Audio, MediaAssetRole.SceneSpeechTrack, selectedOnly: false, cancellationToken);

    public async Task<bool> HasActiveGenerationJobAsync(int sceneId, MediaType mediaType, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.GenerationJobs
            .AsNoTracking()
            .AnyAsync(job =>
                job.SceneId == sceneId &&
                job.MediaType == mediaType &&
                (job.Status == GenerationJobStatus.Pending ||
                 job.Status == GenerationJobStatus.Queued ||
                 job.Status == GenerationJobStatus.Running),
                cancellationToken);
    }

    public async Task<IReadOnlyList<SceneSpeechSegment>> GetSpeechSegmentsAsync(int sceneId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.SceneSpeechSegments
            .AsNoTracking()
            .Include(segment => segment.SceneSpeechPlan)
            .Where(segment => segment.SceneSpeechPlan.SceneId == sceneId)
            .OrderBy(segment => segment.SortOrder)
            .ToListAsync(cancellationToken);
    }

    public async Task MarkHeartbeatAsync(int runId, string message, double? overallProgressPercentage = null, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        run.LastHeartbeatAtUtc = DateTime.UtcNow;
        run.UpdatedAtUtc = run.LastHeartbeatAtUtc;
        run.LastMessage = message;
        if (overallProgressPercentage is not null)
        {
            run.OverallProgressPercentage = Math.Clamp(overallProgressPercentage.Value, 0, 99.9);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task TransitionAsync(int runId, AutonomousGenerationRunStatus status, string message, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        if (!_stateMachine.CanTransition(run.Status, status))
        {
            throw new InvalidOperationException($"Geçersiz otonom durum geçişi: {run.Status} -> {status}.");
        }

        var now = DateTime.UtcNow;
        run.Status = status;
        run.CurrentStage = _stateMachine.ToStage(status);
        run.UpdatedAtUtc = now;
        run.LastHeartbeatAtUtc = now;
        run.LastMessage = message;
        if (status == AutonomousGenerationRunStatus.Failed)
        {
            run.CompletedAtUtc = now;
        }

        if (_stateMachine.IsTerminal(status) || status == AutonomousGenerationRunStatus.Paused)
        {
            run.WorkerId = null;
            run.LeaseExpiresAtUtc = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SetCurrentSceneAsync(int runId, int? sceneId, int? sceneNumber, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        run.CurrentSceneId = sceneId;
        run.CurrentSceneNumber = sceneNumber;
        run.LastHeartbeatAtUtc = DateTime.UtcNow;
        run.UpdatedAtUtc = run.LastHeartbeatAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task MarkWorkItemImageAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default) =>
        MarkWorkItemAsync(workItemId, item =>
        {
            item.ImageStatus = status;
            item.ImageMediaAssetId = mediaAssetId ?? item.ImageMediaAssetId;
            if (incrementAttempt) item.ImageAttemptCount++;
        }, status, error, cancellationToken);

    public Task MarkWorkItemVideoAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default) =>
        MarkWorkItemAsync(workItemId, item =>
        {
            item.VideoStatus = status;
            item.VideoMediaAssetId = mediaAssetId ?? item.VideoMediaAssetId;
            if (incrementAttempt) item.VideoAttemptCount++;
        }, status, error, cancellationToken);

    public Task MarkWorkItemAudioAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default) =>
        MarkWorkItemAsync(workItemId, item =>
        {
            item.AudioStatus = status;
            item.AudioMediaAssetId = mediaAssetId ?? item.AudioMediaAssetId;
            if (incrementAttempt) item.AudioAttemptCount++;
        }, status, error, cancellationToken);

    public Task MarkWorkItemFinalizationAsync(int workItemId, AutonomousWorkItemStatus status, string? error, CancellationToken cancellationToken = default) =>
        MarkWorkItemAsync(workItemId, item => item.FinalizationStatus = status, status, error, cancellationToken);

    public async Task CompleteRunAsync(int runId, string message, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        var now = DateTime.UtcNow;
        run.Status = AutonomousGenerationRunStatus.Completed;
        run.CurrentStage = AutonomousGenerationStage.Completed;
        run.OverallProgressPercentage = 100;
        run.StageProgressPercentage = 100;
        run.CompletedAtUtc = now;
        run.UpdatedAtUtc = now;
        run.LastHeartbeatAtUtc = now;
        run.LastMessage = message;
        run.LastError = string.Empty;
        run.WorkerId = null;
        run.LeaseExpiresAtUtc = null;

        var project = await dbContext.FilmProjects.FirstOrDefaultAsync(item => item.Id == run.FilmProjectId, cancellationToken);
        if (project is not null)
        {
            project.Status = FilmProjectStatus.Completed;
            project.UpdatedAt = DateTime.Now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task FailRunAsync(int runId, string error, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        var now = DateTime.UtcNow;
        if (_stateMachine.CanTransition(run.Status, AutonomousGenerationRunStatus.Failed))
        {
            run.Status = AutonomousGenerationRunStatus.Failed;
            run.CurrentStage = AutonomousGenerationStage.Failed;
        }

        run.CompletedAtUtc = now;
        run.UpdatedAtUtc = now;
        run.LastHeartbeatAtUtc = now;
        run.LastError = error;
        run.LastMessage = "Otonom üretim hata ile durdu.";
        run.WorkerId = null;
        run.LeaseExpiresAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RequestCancellationAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        if (_stateMachine.IsTerminal(run.Status))
        {
            return;
        }

        run.CancellationRequested = true;
        if (_stateMachine.CanTransition(run.Status, AutonomousGenerationRunStatus.CancelRequested))
        {
            run.Status = AutonomousGenerationRunStatus.CancelRequested;
            run.CurrentStage = AutonomousGenerationStage.CancelRequested;
        }

        run.UpdatedAtUtc = DateTime.UtcNow;
        run.LastMessage = "İptal isteği alındı; güvenli checkpoint bekleniyor.";
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task PauseAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        if (_stateMachine.IsTerminal(run.Status))
        {
            return;
        }

        if (_stateMachine.CanTransition(run.Status, AutonomousGenerationRunStatus.Paused))
        {
            run.Status = AutonomousGenerationRunStatus.Paused;
            run.CurrentStage = AutonomousGenerationStage.Paused;
            run.LastMessage = "Otonom üretim duraklatıldı.";
            run.WorkerId = null;
            run.LeaseExpiresAtUtc = null;
        }

        run.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ResumeAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        if (run.Status != AutonomousGenerationRunStatus.Paused)
        {
            return;
        }

        run.CurrentStage = AutonomousGenerationStage.Pending;
        run.WorkerId = null;
        run.LeaseExpiresAtUtc = null;
        run.LastMessage = "Otonom üretim devam etmek üzere kuyruğa alındı.";
        run.UpdatedAtUtc = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RetryAsync(int runId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await RequireRunAsync(dbContext, runId, cancellationToken);
        if (run.Status is not (AutonomousGenerationRunStatus.Failed or AutonomousGenerationRunStatus.Cancelled))
        {
            return;
        }

        var snapshot = DeserializeSnapshot(run);
        EnsureSnapshotVideoDurationCompatible(snapshot);
        var filmProjectId = run.FilmProjectId;
        await StartOrGetActiveRunAsync(filmProjectId, snapshot, cancellationToken);
        return;

    }

    public Task<SceneMediaAsset?> FindValidImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
        FindValidAssetAsync(sceneId, MediaType.Image, null, false, cancellationToken);

    private async Task<SceneMediaAsset?> FindValidAssetAsync(
        int sceneId,
        MediaType mediaType,
        MediaAssetRole? role,
        bool selectedOnly,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.SceneMediaAssets
            .AsNoTracking()
            .Where(asset => asset.SceneId == sceneId && asset.MediaType == mediaType);

        if (role is not null)
        {
            query = query.Where(asset => asset.Role == role);
        }

        if (selectedOnly)
        {
            query = query.Where(asset => asset.IsSelected);
        }

        var candidates = await query
            .OrderByDescending(asset => asset.IsSelected)
            .ThenByDescending(asset => asset.CreatedAt)
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(asset =>
            !string.IsNullOrWhiteSpace(asset.FilePath) &&
            File.Exists(asset.FilePath) &&
            new FileInfo(asset.FilePath).Length > 0);
    }

    private async Task MarkWorkItemAsync(
        int workItemId,
        Action<AutonomousSceneWorkItem> apply,
        AutonomousWorkItemStatus status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await dbContext.AutonomousSceneWorkItems
            .FirstOrDefaultAsync(workItem => workItem.Id == workItemId, cancellationToken)
            ?? throw new InvalidOperationException("Otonom sahne iş öğesi bulunamadı.");

        var now = DateTime.UtcNow;
        if (status == AutonomousWorkItemStatus.Running && item.StartedAtUtc is null)
        {
            item.StartedAtUtc = now;
        }

        if (status is AutonomousWorkItemStatus.Completed or AutonomousWorkItemStatus.Failed or AutonomousWorkItemStatus.Skipped or AutonomousWorkItemStatus.Cancelled)
        {
            item.CompletedAtUtc = now;
        }

        apply(item);
        item.LastError = error ?? string.Empty;
        item.UpdatedAtUtc = now;
        var run = await dbContext.AutonomousGenerationRuns
            .Include(run => run.WorkItems)
            .FirstOrDefaultAsync(run => run.Id == item.AutonomousGenerationRunId, cancellationToken);
        if (run is not null)
        {
            run.CompletedSceneCount = run.WorkItems.Count(IsSceneWorkItemCompleted);
            run.TotalSceneCount = Math.Max(run.TotalSceneCount, run.WorkItems.Count);
            run.UpdatedAtUtc = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool IsSceneWorkItemCompleted(AutonomousSceneWorkItem item) =>
        IsWorkItemTerminalSuccess(item.VideoStatus) &&
        IsWorkItemTerminalSuccess(item.FinalizationStatus);

    private static bool IsWorkItemTerminalSuccess(AutonomousWorkItemStatus status) =>
        status is AutonomousWorkItemStatus.Completed or AutonomousWorkItemStatus.Skipped;

    private static bool HasValidStory(FilmStory? story) =>
        story is not null &&
        !string.IsNullOrWhiteSpace(story.Title) &&
        !string.IsNullOrWhiteSpace(story.Synopsis);

    private static bool HasValidCharacters(FilmStory? story) =>
        story?.Characters.Any(character =>
            !string.IsNullOrWhiteSpace(character.CharacterKey) &&
            !string.IsNullOrWhiteSpace(character.Name) &&
            !string.IsNullOrWhiteSpace(character.PhysicalDescription) &&
            !string.IsNullOrWhiteSpace(character.ContinuityDescription)) == true;

    private static SceneMediaAsset? FindValidAsset(
        IEnumerable<SceneMediaAsset> assets,
        int sceneId,
        MediaType mediaType,
        MediaAssetRole? role,
        bool selectedOnly)
    {
        var query = assets.Where(asset => asset.SceneId == sceneId && asset.MediaType == mediaType);
        if (role is not null)
        {
            query = query.Where(asset => asset.Role == role);
        }

        if (selectedOnly)
        {
            query = query.Where(asset => asset.IsSelected);
        }

        return query.FirstOrDefault(asset =>
            !string.IsNullOrWhiteSpace(asset.FilePath) &&
            File.Exists(asset.FilePath) &&
            new FileInfo(asset.FilePath).Length > 0);
    }

    private static async Task<AutonomousGenerationRun> RequireRunAsync(AppDbContext dbContext, int runId, CancellationToken cancellationToken) =>
        await dbContext.AutonomousGenerationRuns.FirstOrDefaultAsync(run => run.Id == runId, cancellationToken)
        ?? throw new InvalidOperationException("Otonom çalışma bulunamadı.");

    private void EnsureSnapshotVideoDurationCompatible(AutonomousGenerationConfigurationSnapshot snapshot)
    {
        var validation = _videoModelCapabilityService.ValidateSnapshot(
            snapshot.VideoModelType,
            snapshot.ClipDurationSeconds,
            snapshot.CalculatedClipCount);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Otonom run snapshot video suresi secili modelle uyumsuz. " + validation.ErrorMessage);
        }
    }

    private static AutonomousGenerationConfigurationSnapshot DeserializeSnapshot(AutonomousGenerationRun run)
    {
        if (string.IsNullOrWhiteSpace(run.ConfigurationSnapshotJson))
        {
            return InvalidFallbackSnapshot(run.FilmProjectId);
        }

        return JsonSerializer.Deserialize<AutonomousGenerationConfigurationSnapshot>(run.ConfigurationSnapshotJson, JsonOptions)
            ?? InvalidFallbackSnapshot(run.FilmProjectId);
    }

    private static AutonomousGenerationConfigurationSnapshot InvalidFallbackSnapshot(int filmProjectId) => new()
    {
        FilmProjectId = filmProjectId,
        VideoModelType = VideoModelCapabilityService.VerifiedLtxModelType,
        ClipDurationSeconds = 0,
        CalculatedClipCount = 0
    };

    private static AutonomousGenerationRunSummary ToSummary(AutonomousGenerationRun run) => new()
    {
        Id = run.Id,
        FilmProjectId = run.FilmProjectId,
        Status = run.Status,
        CurrentStage = run.CurrentStage,
        CurrentSceneNumber = run.CurrentSceneNumber,
        TotalSceneCount = run.TotalSceneCount,
        CompletedSceneCount = run.CompletedSceneCount,
        OverallProgressPercentage = run.OverallProgressPercentage,
        StageProgressPercentage = run.StageProgressPercentage,
        StartedAtUtc = run.StartedAtUtc,
        UpdatedAtUtc = run.UpdatedAtUtc,
        CompletedAtUtc = run.CompletedAtUtc,
        LastHeartbeatAtUtc = run.LastHeartbeatAtUtc,
        LastMessage = run.LastMessage,
        LastError = run.LastError ?? string.Empty,
        CancellationRequested = run.CancellationRequested
    };
}
