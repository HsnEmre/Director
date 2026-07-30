namespace Director.Ollama;

public interface IOllamaClient
{
    Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default);

    Task<TResponse> ChatStructuredAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages,
        object jsonSchema,
        CancellationToken cancellationToken = default);
}
