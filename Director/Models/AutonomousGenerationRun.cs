using Director.Enums;

namespace Director.Models;

public sealed class AutonomousGenerationRun
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;
    public AutonomousGenerationRunStatus Status { get; set; } = AutonomousGenerationRunStatus.Pending;
    public AutonomousGenerationStage CurrentStage { get; set; } = AutonomousGenerationStage.Pending;
    public int? CurrentSceneId { get; set; }
    public FilmScene? CurrentScene { get; set; }
    public int? CurrentSceneNumber { get; set; }
    public int TotalSceneCount { get; set; }
    public int CompletedSceneCount { get; set; }
    public double OverallProgressPercentage { get; set; }
    public double StageProgressPercentage { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime LastHeartbeatAtUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public int AttemptCount { get; set; }
    public string? WorkerId { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LastError { get; set; }
    public string ConfigurationSnapshotJson { get; set; } = "{}";
    public string CorrelationId { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;

    public ICollection<AutonomousSceneWorkItem> WorkItems { get; set; } = new List<AutonomousSceneWorkItem>();
}
