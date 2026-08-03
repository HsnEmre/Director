namespace Director.Ollama;

public interface IOllamaClient
{
    Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default);

    Task<TResponse> ChatStructuredAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages,
        object jsonSchema,
        string? modelOverride = null,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default,
        IProgress<OllamaStreamProgress>? streamProgress = null,
        OllamaGenerationSettings? generationSettings = null);

    Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages,
        object jsonSchema,
        string? modelOverride = null,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default,
        IProgress<OllamaStreamProgress>? streamProgress = null,
        OllamaGenerationSettings? generationSettings = null);
}
