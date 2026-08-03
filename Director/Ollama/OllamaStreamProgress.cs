namespace Director.Ollama;

public enum OllamaStreamStage
{
    RequestStarted,
    ModelPreparing,
    FirstContentChunk,
    ContentChunk,
    ActivityHeartbeat,
    Completed,
    JsonValidating
}

public sealed class OllamaStreamProgress
{
    public OllamaStreamStage Stage { get; set; }
    public string Model { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
    public TimeSpan TimeSinceLastActivity { get; set; }
    public int ContentChunkCount { get; set; }
    public int PromptTokenCount { get; set; }
    public int ResponseTokenCount { get; set; }
    public bool Done { get; set; }
    public string DoneReason { get; set; } = string.Empty;
    public int ResponseCharacterCount { get; set; }
    public TimeSpan LoadDuration { get; set; }
    public TimeSpan EvaluationDuration { get; set; }
}
