namespace Director.Ollama;

public abstract class OllamaResponseException : InvalidOperationException
{
    protected OllamaResponseException(
        string message,
        string stage,
        string responseContent,
        OllamaResponseMetadata metadata,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        ResponseContent = responseContent;
        Metadata = metadata;
        if (innerException is System.Text.Json.JsonException jsonException)
        {
            JsonPath = jsonException.Path;
            LineNumber = jsonException.LineNumber;
            BytePositionInLine = jsonException.BytePositionInLine;
        }
    }

    public string Stage { get; }
    public string ResponseContent { get; }
    public OllamaResponseMetadata Metadata { get; }
    public string? JsonPath { get; }
    public long? LineNumber { get; }
    public long? BytePositionInLine { get; }
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];
}

public sealed class OllamaStructuredResponseException : OllamaResponseException
{
    public OllamaStructuredResponseException(
        string message,
        string stage,
        string responseContent,
        OllamaResponseMetadata metadata,
        Exception? innerException = null)
        : base(message, stage, responseContent, metadata, innerException)
    {
    }
}

public sealed class OllamaIncompleteStreamException : OllamaResponseException
{
    public OllamaIncompleteStreamException(string message, string responseContent, OllamaResponseMetadata metadata, Exception? innerException = null)
        : base(message, "StreamCompletion", responseContent, metadata, innerException)
    {
    }
}

public sealed class OllamaHttpResponseException : OllamaResponseException
{
    public OllamaHttpResponseException(string message, string responseContent, OllamaResponseMetadata metadata)
        : base(message, "HttpResponse", responseContent, metadata)
    {
    }
}

public sealed class OllamaResponseTruncatedException : OllamaResponseException
{
    public OllamaResponseTruncatedException(string message, string responseContent, OllamaResponseMetadata metadata)
        : base(message, "TokenLimit", responseContent, metadata)
    {
    }
}

public sealed class OllamaRepetitionDetectedException : OllamaResponseException
{
    public OllamaRepetitionDetectedException(string message, string responseContent, OllamaResponseMetadata metadata)
        : base(message, "RepetitionDetected", responseContent, metadata)
    {
    }
}

public sealed class OllamaResponseTooLargeException : OllamaResponseException
{
    public OllamaResponseTooLargeException(string message, string responseContent, OllamaResponseMetadata metadata)
        : base(message, "ResponseTooLarge", responseContent, metadata)
    {
    }
}
