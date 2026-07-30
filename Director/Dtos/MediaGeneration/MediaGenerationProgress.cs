using Director.Enums;

namespace Director.Dtos.MediaGeneration;

public sealed class MediaGenerationProgress
{
    public double OverallProgress { get; set; }
    public double SceneProgress { get; set; }
    public string Phase { get; set; } = string.Empty;
    public GenerationJobStatus Status { get; set; }
    public int? CurrentStep { get; set; }
    public int? TotalSteps { get; set; }
    public int CurrentSceneNumber { get; set; }
    public int TotalScenes { get; set; }
    public string ModelType { get; set; } = string.Empty;
    public string? ExternalJobId { get; set; }
    public string? PreviewPath { get; set; }
    public GenerationLogLevel Level { get; set; } = GenerationLogLevel.Information;
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
}
