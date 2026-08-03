using System.Text.Json;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;

namespace Director.Tests;

public sealed class OllamaFailureDiagnosticTests
{
    [Fact]
    public async Task DiagnosticWriter_PersistsBoundedRawResponseAndFailureMetadataOutsideRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorTests", Guid.NewGuid().ToString("N"));
        var writer = new OllamaFailureDiagnosticWriter(root);
        var metadata = new OllamaResponseMetadata
        {
            Model = "qwen3-vl:30b-a3b-instruct",
            Done = true,
            StreamCompleted = true,
            DoneReason = "length",
            PromptTokenCount = 1710,
            ResponseTokenCount = 4096,
            ResponseCharacterCount = 8923
        };
        var exception = new OllamaResponseTruncatedException("truncated", "{\"partial\":\"raw", metadata);

        var path = await writer.WriteAsync(
            new OllamaFailureContext(9, 14, "SingleSceneGeneration"),
            "initial",
            exception,
            CancellationToken.None);

        Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(9, document.RootElement.GetProperty("filmProjectId").GetInt32());
        Assert.Equal(14, document.RootElement.GetProperty("sceneNumber").GetInt32());
        Assert.Equal("length", document.RootElement.GetProperty("doneReason").GetString());
        Assert.Equal("{\"partial\":\"raw", document.RootElement.GetProperty("assembledRawResponse").GetString());
        Assert.Equal("{\"partial\":\"raw".Length, document.RootElement.GetProperty("originalCharacterCount").GetInt32());
        Assert.False(document.RootElement.GetProperty("wasTruncated").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("fullAssembledRawResponse", out _));
    }

    [Fact]
    public async Task DiagnosticWriter_TruncatesLargeRawResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorTests", Guid.NewGuid().ToString("N"));
        var writer = new OllamaFailureDiagnosticWriter(
            root,
            new OllamaOptions
            {
                DiagnosticMaxRawResponseCharacters = 1024
            });
        var raw = new string('a', 1500) + "TAIL";
        var exception = new OllamaResponseTooLargeException(
            "too large",
            raw,
            new OllamaResponseMetadata
            {
                Model = "qwen3-vl:30b-a3b-instruct",
                ResponseCharacterCount = raw.Length,
                ConfiguredResponseLimit = 1024
            });

        var path = await writer.WriteAsync(
            new OllamaFailureContext(9, 1, "SingleSceneGeneration"),
            "initial",
            exception,
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var stored = document.RootElement.GetProperty("assembledRawResponse").GetString()!;
        Assert.True(document.RootElement.GetProperty("wasTruncated").GetBoolean());
        Assert.Equal(raw.Length, document.RootElement.GetProperty("originalCharacterCount").GetInt32());
        Assert.True(stored.Length <= 1024);
        Assert.Contains("structured response truncated", stored);
        Assert.EndsWith("TAIL", stored);
    }

    [Fact]
    public async Task DiagnosticWriter_UsesCollisionResistantFileNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorTests", Guid.NewGuid().ToString("N"));
        var writer = new OllamaFailureDiagnosticWriter(root);
        var exception = new OllamaResponseTruncatedException(
            "truncated",
            "{\"partial\":true}",
            new OllamaResponseMetadata { Model = "qwen3-vl:30b-a3b-instruct" });

        var firstPath = await writer.WriteAsync(new OllamaFailureContext(9, 1, "SingleSceneGeneration"), "repair", exception, CancellationToken.None);
        var secondPath = await writer.WriteAsync(new OllamaFailureContext(9, 1, "SingleSceneGeneration"), "repair", exception, CancellationToken.None);

        Assert.NotEqual(firstPath, secondPath);
        Assert.True(File.Exists(firstPath));
        Assert.True(File.Exists(secondPath));
    }

    [Fact]
    public async Task DiagnosticWriter_AppliesFileCountRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        for (var index = 0; index < 5; index++)
        {
            var path = Path.Combine(root, $"old-{index}.json");
            await File.WriteAllTextAsync(path, "{}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 - index));
        }

        var writer = new OllamaFailureDiagnosticWriter(
            root,
            new OllamaOptions
            {
                DiagnosticRetentionMaxFiles = 2,
                DiagnosticRetentionMaxAgeDays = 30
            });
        var exception = new OllamaResponseTruncatedException(
            "truncated",
            "{\"partial\":true}",
            new OllamaResponseMetadata { Model = "qwen3-vl:30b-a3b-instruct" });

        await writer.WriteAsync(new OllamaFailureContext(9, 1, "SingleSceneGeneration"), "initial", exception, CancellationToken.None);

        Assert.True(Directory.EnumerateFiles(root, "*.json").Count() <= 2);
    }
}
