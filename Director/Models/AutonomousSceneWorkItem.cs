using Director.Enums;

namespace Director.Models;

public sealed class AutonomousSceneWorkItem
{
    public int Id { get; set; }
    public int AutonomousGenerationRunId { get; set; }
    public AutonomousGenerationRun AutonomousGenerationRun { get; set; } = null!;
    public int StorySceneId { get; set; }
    public FilmScene StoryScene { get; set; } = null!;
    public int SceneNumber { get; set; }
    public AutonomousWorkItemStatus ImageStatus { get; set; } = AutonomousWorkItemStatus.Pending;
    public int ImageAttemptCount { get; set; }
    public int? ImageMediaAssetId { get; set; }
    public SceneMediaAsset? ImageMediaAsset { get; set; }
    public AutonomousWorkItemStatus VideoStatus { get; set; } = AutonomousWorkItemStatus.Pending;
    public int VideoAttemptCount { get; set; }
    public int? VideoMediaAssetId { get; set; }
    public SceneMediaAsset? VideoMediaAsset { get; set; }
    public AutonomousWorkItemStatus AudioStatus { get; set; } = AutonomousWorkItemStatus.Pending;
    public int AudioAttemptCount { get; set; }
    public int? AudioMediaAssetId { get; set; }
    public SceneMediaAsset? AudioMediaAsset { get; set; }
    public AutonomousWorkItemStatus FinalizationStatus { get; set; } = AutonomousWorkItemStatus.Pending;
    public string? LastError { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
