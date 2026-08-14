using Director.Dtos.Autonomous;
using Director.Enums;
using Director.Models;

namespace Director.Services.Interfaces;

public interface IAutonomousGenerationRunService
{
    Task<AutonomousGenerationRunSummary> StartOrGetActiveRunAsync(
        int filmProjectId,
        AutonomousGenerationConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<AutonomousGenerationRun?> GetRunAsync(int runId, CancellationToken cancellationToken = default);
    Task<AutonomousGenerationRunSummary?> GetLatestRunForProjectAsync(int filmProjectId, CancellationToken cancellationToken = default);
    Task<AutonomousProjectCheckpoint> GetProjectCheckpointAsync(int filmProjectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutonomousGenerationRunSummary>> GetRunnableRunsAsync(CancellationToken cancellationToken = default);
    Task<bool> TryClaimRunAsync(
        int runId,
        string workerId,
        TimeSpan staleHeartbeatThreshold,
        TimeSpan leaseExtension,
        CancellationToken cancellationToken = default);
    Task<bool> TryRenewLeaseAsync(
        int runId,
        string workerId,
        TimeSpan leaseExtension,
        string message,
        CancellationToken cancellationToken = default);
    Task<bool> IsRunOwnedByWorkerAsync(int runId, string workerId, CancellationToken cancellationToken = default);
    Task ReleaseClaimAsync(int runId, string workerId, CancellationToken cancellationToken = default);
    Task<FilmProject> GetProjectAsync(int filmProjectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FilmScene>> GetScenesAsync(int filmProjectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutonomousSceneWorkItem>> EnsureSceneWorkItemsAsync(int runId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset?> FindValidImageAssetAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset?> FindValidSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset?> FindValidSelectedVideoAssetAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset?> FindValidSceneAudioAssetAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<bool> HasActiveGenerationJobAsync(int sceneId, MediaType mediaType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SceneSpeechSegment>> GetSpeechSegmentsAsync(int sceneId, CancellationToken cancellationToken = default);
    Task MarkHeartbeatAsync(int runId, string message, double? overallProgressPercentage = null, CancellationToken cancellationToken = default);
    Task TransitionAsync(int runId, AutonomousGenerationRunStatus status, string message, CancellationToken cancellationToken = default);
    Task SetCurrentSceneAsync(int runId, int? sceneId, int? sceneNumber, CancellationToken cancellationToken = default);
    Task MarkWorkItemImageAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default);
    Task MarkWorkItemVideoAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default);
    Task MarkWorkItemAudioAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default);
    Task MarkWorkItemFinalizationAsync(int workItemId, AutonomousWorkItemStatus status, string? error, CancellationToken cancellationToken = default);
    Task CompleteRunAsync(int runId, string message, CancellationToken cancellationToken = default);
    Task FailRunAsync(int runId, string error, CancellationToken cancellationToken = default);
    Task RequestCancellationAsync(int runId, CancellationToken cancellationToken = default);
    Task PauseAsync(int runId, CancellationToken cancellationToken = default);
    Task ResumeAsync(int runId, CancellationToken cancellationToken = default);
    Task RetryAsync(int runId, CancellationToken cancellationToken = default);
}
