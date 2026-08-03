using Director.Ollama;

namespace Director.Services.Interfaces;

public interface IOllamaFailureDiagnosticWriter
{
    Task<string> WriteAsync(
        OllamaFailureContext context,
        string attemptType,
        OllamaResponseException exception,
        CancellationToken cancellationToken = default);
}

public sealed record OllamaFailureContext(
    int FilmProjectId,
    int SceneNumber,
    string OperationName,
    int? SceneId = null,
    int? StoryCharacterId = null,
    string? CharacterKey = null,
    string? CorrelationId = null);
