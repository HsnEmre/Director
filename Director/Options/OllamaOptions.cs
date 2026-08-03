namespace Director.Options;

public sealed class OllamaOptions
{
    public const string DefaultTextModel = "qwen3-vl:30b-a3b-instruct";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = DefaultTextModel;
    public string StoryTextModel { get; set; } = DefaultTextModel;
    public string SceneTextModel { get; set; } = DefaultTextModel;
    public string PromptPreparationModel { get; set; } = DefaultTextModel;
    public string DialogueModel { get; set; } = DefaultTextModel;
    public string VisualPromptModel { get; set; } = DefaultTextModel;
    public string VideoPromptModel { get; set; } = DefaultTextModel;
    public string KeepAlive { get; set; } = "30m";
    public int RequestTimeoutMinutes { get; set; } = 30;
    public int SceneConnectTimeoutSeconds { get; set; } = 15;
    public int SceneFirstTokenTimeoutSeconds { get; set; } = 600;
    public int SceneNoActivityTimeoutSeconds { get; set; } = 120;
    public int SceneHardTimeoutMinutes { get; set; } = 45;
    public double Temperature { get; set; } = 0.65;
    public double TopP { get; set; } = 0.9;
    public int ContextLength { get; set; } = 32768;
    public int SceneBatchSize { get; set; } = 1;
    public int SceneNumPredict { get; set; } = 6144;
    public int SceneRepairNumPredict { get; set; } = 6144;
    public int MaxStructuredResponseCharacters { get; set; } = 262_144;
    public int DiagnosticMaxRawResponseCharacters { get; set; } = 65_536;
    public int DiagnosticRetentionMaxFiles { get; set; } = 100;
    public int DiagnosticRetentionMaxAgeDays { get; set; } = 30;
}
