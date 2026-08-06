using System.IO;
using System.Text.Json;
using Director.Ollama;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class OllamaFailureDiagnosticWriter : IOllamaFailureDiagnosticWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string TruncationMarker = "\n\n...[structured response truncated for diagnostic size limit]...\n\n";
    private readonly string _rootPath;
    private readonly int _maxRawResponseCharacters;
    private readonly int _retentionMaxFiles;
    private readonly TimeSpan _retentionMaxAge;

    public OllamaFailureDiagnosticWriter()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Director",
            "Logs",
            "OllamaFailures"),
            new OllamaOptions())
    {
    }

    public OllamaFailureDiagnosticWriter(IOptions<OllamaOptions> options)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Director",
            "Logs",
            "OllamaFailures"),
            options.Value)
    {
    }

    public OllamaFailureDiagnosticWriter(string rootPath)
        : this(rootPath, new OllamaOptions())
    {
    }

    public OllamaFailureDiagnosticWriter(string rootPath, OllamaOptions options)
    {
        _rootPath = rootPath;
        _maxRawResponseCharacters = Math.Max(1024, options.DiagnosticMaxRawResponseCharacters);
        _retentionMaxFiles = Math.Max(1, options.DiagnosticRetentionMaxFiles);
        _retentionMaxAge = TimeSpan.FromDays(Math.Max(1, options.DiagnosticRetentionMaxAgeDays));
    }

    public async Task<string> WriteAsync(
        OllamaFailureContext context,
        string attemptType,
        OllamaResponseException exception,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_rootPath);
        var timestamp = DateTimeOffset.Now;
        CleanupBestEffort(timestamp);
        var safeAttempt = SanitizeFilePart(attemptType);
        var fileName = $"{timestamp:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}-project-{context.FilmProjectId}-scene-{context.SceneNumber}-{safeAttempt}.json";
        var path = Path.Combine(_rootPath, fileName);
        var storedRawResponse = LimitRawResponse(exception.ResponseContent, _maxRawResponseCharacters, out var wasTruncated);
        var payload = new
        {
            timestamp,
            context.FilmProjectId,
            context.SceneId,
            context.SceneNumber,
            context.StoryCharacterId,
            context.CharacterKey,
            diagnosticCorrelationId = context.CorrelationId,
            operationName = context.OperationName,
            attemptType,
            selectedModel = exception.Metadata.Model,
            httpEndpoint = exception.Metadata.Endpoint,
            streamCompleted = exception.Metadata.StreamCompleted,
            done = exception.Metadata.Done,
            doneReason = exception.Metadata.DoneReason,
            promptTokenCount = exception.Metadata.PromptTokenCount,
            responseTokenCount = exception.Metadata.ResponseTokenCount,
            responseCharacterCount = exception.Metadata.ResponseCharacterCount,
            originalCharacterCount = exception.ResponseContent.Length,
            storedCharacterCount = storedRawResponse.Length,
            wasTruncated,
            responseStorageLimit = _maxRawResponseCharacters,
            contentChunkCount = exception.Metadata.ContentChunkCount,
            repeatedBlockLength = exception.Metadata.RepeatedBlockLength,
            repeatedBlockCount = exception.Metadata.RepeatedBlockCount,
            repeatedBlockPreview = exception.Metadata.RepeatedBlockPreview,
            elapsedMilliseconds = exception.Metadata.Elapsed.TotalMilliseconds,
            assembledRawResponse = storedRawResponse,
            failureStage = exception.Stage,
            parserExceptionType = exception.InnerException?.GetType().FullName ?? exception.GetType().FullName,
            exceptionMessage = exception.Message,
            jsonPath = exception.JsonPath,
            lineNumber = exception.LineNumber,
            bytePositionInLine = exception.BytePositionInLine,
            validationErrors = exception.ValidationErrors
        };
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
        File.Move(tempPath, path, overwrite: false);
        CleanupBestEffort(DateTimeOffset.Now);
        return path;
    }

    private static string LimitRawResponse(string value, int maxCharacters, out bool wasTruncated)
    {
        wasTruncated = value.Length > maxCharacters;
        if (!wasTruncated)
        {
            return value;
        }

        var remaining = Math.Max(0, maxCharacters - TruncationMarker.Length);
        var headLength = remaining / 2;
        var tailLength = remaining - headLength;
        return value[..headLength] + TruncationMarker + value[^tailLength..];
    }

    private void CleanupBestEffort(DateTimeOffset now)
    {
        try
        {
            var files = Directory.EnumerateFiles(_rootPath, "*.json")
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
            var cutoff = now.UtcDateTime - _retentionMaxAge;
            foreach (var file in files.Skip(_retentionMaxFiles).Concat(files.Where(file => file.LastWriteTimeUtc < cutoff)).DistinctBy(file => file.FullName))
            {
                TryDelete(file);
            }
        }
        catch
        {
            // Retention is best-effort and must never mask the original model failure.
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // A file may be in use or access may be denied; keep production flow alive.
        }
    }

    private static string SanitizeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray();
        return new string(chars);
    }
}
