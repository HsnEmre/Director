namespace Director.Ollama;

public sealed class OllamaResponseMetadata
{
    public string Model { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "/api/chat";
    public string OperationName { get; set; } = string.Empty;
    public int? FilmProjectId { get; set; }
    public int? SceneNumber { get; set; }
    public int ConfiguredResponseLimit { get; set; }
    public bool StreamCompleted { get; set; }
    public bool Done { get; set; }
    public string DoneReason { get; set; } = string.Empty;
    public int PromptTokenCount { get; set; }
    public int ResponseTokenCount { get; set; }
    public int ContentChunkCount { get; set; }
    public int ResponseCharacterCount { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan LoadDuration { get; set; }
    public TimeSpan EvaluationDuration { get; set; }
}

public sealed class OllamaStructuredResult<T>
{
    public required T Value { get; init; }
    public required string RawResponse { get; init; }
    public required string NormalizedJson { get; init; }
    public required OllamaResponseMetadata Metadata { get; init; }
}

public sealed class OllamaGenerationSettings
{
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? NumPredict { get; set; }
    public bool Think { get; set; }
    public string? OperationName { get; set; }
    public int? FilmProjectId { get; set; }
    public int? SceneNumber { get; set; }
}
