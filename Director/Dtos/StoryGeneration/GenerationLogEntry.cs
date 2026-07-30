using Director.Enums;

namespace Director.Dtos.StoryGeneration;

public sealed class GenerationLogEntry
{
    public DateTime Timestamp { get; set; }
    public GenerationLogLevel Level { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? SceneStart { get; set; }
    public int? SceneEnd { get; set; }
    public double? Percentage { get; set; }
}
