using Director.Enums;

namespace Director.Dtos.Autonomous;

public sealed class AutonomousGenerationRunSummary
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public AutonomousGenerationRunStatus Status { get; set; }
    public AutonomousGenerationStage CurrentStage { get; set; }
    public int? CurrentSceneNumber { get; set; }
    public int TotalSceneCount { get; set; }
    public int CompletedSceneCount { get; set; }
    public double OverallProgressPercentage { get; set; }
    public double StageProgressPercentage { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? LastHeartbeatAtUtc { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public string LastError { get; set; } = string.Empty;
    public bool CancellationRequested { get; set; }
    public bool IsActive => Status is
        AutonomousGenerationRunStatus.Pending or
        AutonomousGenerationRunStatus.Validating or
        AutonomousGenerationRunStatus.GeneratingStoryNarrative or
        AutonomousGenerationRunStatus.GeneratingCharacters or
        AutonomousGenerationRunStatus.GeneratingNarrativeScenes or
        AutonomousGenerationRunStatus.GeneratingImagePrompts or
        AutonomousGenerationRunStatus.GeneratingVideoPrompts or
        AutonomousGenerationRunStatus.GeneratingStory or
        AutonomousGenerationRunStatus.GeneratingScenes or
        AutonomousGenerationRunStatus.GeneratingImages or
        AutonomousGenerationRunStatus.GeneratingVideos or
        AutonomousGenerationRunStatus.GeneratingAudio or
        AutonomousGenerationRunStatus.Finalizing or
        AutonomousGenerationRunStatus.CancelRequested or
        AutonomousGenerationRunStatus.Paused;
}
