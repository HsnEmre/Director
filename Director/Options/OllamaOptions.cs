namespace Director.Options;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3-vl:30b-a3b-instruct";
    public string KeepAlive { get; set; } = "30m";
    public int RequestTimeoutMinutes { get; set; } = 30;
    public double Temperature { get; set; } = 0.65;
    public double TopP { get; set; } = 0.9;
    public int ContextLength { get; set; } = 32768;
    public int SceneBatchSize { get; set; } = 5;
}
