namespace Director.Ollama;

public sealed class OllamaHealthResult
{
    public bool IsAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> Models { get; set; } = Array.Empty<string>();
}
