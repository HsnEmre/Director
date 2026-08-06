using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Director.Tests;

public sealed class StoryRepairFlowTests
{
    [Fact]
    public async Task InitialInvalid_RepairValid_UsesExactlyTwoCallsWithSame30BModel()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: true);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var result = await service.GenerateWithOneRepairAsync<RepairResponse>(
            [new OllamaChatMessage("user", "scene")],
            new { type = "object" },
            null,
            "Sahne 14",
            CancellationToken.None,
            OllamaOptions.DefaultTextModel,
            new OllamaFailureContext(9, 14, "SingleSceneGeneration"));

        Assert.Equal("repaired", result.Value);
        Assert.Equal(2, client.CallCount);
        Assert.All(client.Models, model => Assert.Equal(OllamaOptions.DefaultTextModel, model));
        Assert.Single(diagnostics.Attempts);
        Assert.Equal("initial", diagnostics.Attempts[0]);
        Assert.True(client.SecondCallUsedDeterministicSettings);
    }

    [Fact]
    public async Task InitialInvalid_RepairInvalid_StopsAfterTwoCallsAndReturnsTypedSceneError()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: false);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var exception = await Assert.ThrowsAsync<StorySceneGenerationException>(() =>
            service.GenerateWithOneRepairAsync<RepairResponse>(
                [new OllamaChatMessage("user", "scene")],
                new { type = "object" },
                null,
                "Sahne 14",
                CancellationToken.None,
                OllamaOptions.DefaultTextModel,
                new OllamaFailureContext(9, 14, "SingleSceneGeneration")));

        Assert.Equal(2, client.CallCount);
        Assert.Equal(new[] { "initial", "repair" }, diagnostics.Attempts);
        Assert.Equal(9, exception.FilmProjectId);
        Assert.Equal(14, exception.SceneNumber);
        Assert.EndsWith("repair.json", exception.LogPath);
    }

    [Fact]
    public async Task InitialDomainValidationFailure_RepairValid_UsesSameRepairChain()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: true, firstCallReturnsValue: true);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var result = await service.GenerateWithOneRepairAsync<RepairResponse>(
            [new OllamaChatMessage("user", "scene")],
            new { type = "object" },
            null,
            "Sahne 14",
            CancellationToken.None,
            OllamaOptions.DefaultTextModel,
            new OllamaFailureContext(9, 14, "SingleSceneGeneration"),
            value =>
            {
                if (value.Value != "repaired") throw new InvalidOperationException("required field invalid");
            });

        Assert.Equal("repaired", result.Value);
        Assert.Equal(2, client.CallCount);
        Assert.Single(diagnostics.Attempts);
    }

    [Fact]
    public async Task InitialTooLargeFailure_DoesNotAttemptRepair()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: true, firstCallTooLarge: true);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var exception = await Assert.ThrowsAsync<StorySceneGenerationException>(() =>
            service.GenerateWithOneRepairAsync<RepairResponse>(
                [new OllamaChatMessage("user", "scene")],
                new { type = "object" },
                null,
                "Sahne 1",
                CancellationToken.None,
                OllamaOptions.DefaultTextModel,
                new OllamaFailureContext(9, 1, "SingleSceneGeneration")));

        Assert.Equal(1, client.CallCount);
        Assert.Equal(new[] { "initial" }, diagnostics.Attempts);
        Assert.Equal(9, exception.FilmProjectId);
        Assert.Equal(1, exception.SceneNumber);
    }

    [Fact]
    public async Task InitialTokenLimit_UsesFreshRetryWithoutRawResponseEcho()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: true, firstCallTokenLimit: true);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var result = await service.GenerateWithOneRepairAsync<RepairResponse>(
            [new OllamaChatMessage("user", "scene initial prompt")],
            new { type = "object" },
            null,
            "Sahne 25",
            CancellationToken.None,
            OllamaOptions.DefaultTextModel,
            new OllamaFailureContext(9, 25, "SingleSceneGeneration"));

        Assert.Equal("repaired", result.Value);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(new[] { "initial" }, diagnostics.Attempts);
        Assert.DoesNotContain(RepairFlowOllamaClient.TruncatedRawMarker, client.Calls[1].Prompt);
        Assert.Contains("discarded", client.Calls[1].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Malformed response", client.Calls[1].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(client.Calls[1].Prompt.Length <= client.Calls[0].Prompt.Length + 360);
        Assert.Equal(3072, client.Calls[1].Settings?.NumPredict);
    }

    [Fact]
    public async Task InitialTokenLimitFreshTokenLimit_StopsAfterTwoCallsAndUsesNoRepair()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: false, firstCallTokenLimit: true, secondCallTokenLimit: true);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var exception = await Assert.ThrowsAsync<StorySceneGenerationException>(() =>
            service.GenerateWithOneRepairAsync<RepairResponse>(
                [new OllamaChatMessage("user", "scene")],
                new { type = "object" },
                null,
                "Sahne 25",
                CancellationToken.None,
                OllamaOptions.DefaultTextModel,
                new OllamaFailureContext(9, 25, "SingleSceneGeneration")));

        Assert.Equal(2, client.CallCount);
        Assert.Equal(new[] { "initial", "fresh" }, diagnostics.Attempts);
        Assert.Equal("TokenLimit", exception.Stage);
        Assert.DoesNotContain("qwen3:4b", client.Models);
    }

    [Fact]
    public async Task InitialRepetitionDetected_UsesFreshRetry()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: true, firstCallRepetition: true);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        _ = await service.GenerateWithOneRepairAsync<RepairResponse>(
            [new OllamaChatMessage("user", "scene")],
            new { type = "object" },
            null,
            "Sahne 25",
            CancellationToken.None,
            OllamaOptions.DefaultTextModel,
            new OllamaFailureContext(9, 25, "SingleSceneGeneration"));

        Assert.Equal(2, client.CallCount);
        Assert.DoesNotContain("Malformed response", client.Calls[1].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3072, client.Calls[1].Settings?.NumPredict);
    }

    [Fact]
    public async Task DiagnosticWriterFailure_DoesNotMaskOriginalModelFailure()
    {
        var client = new RepairFlowOllamaClient(repairSucceeds: true, firstCallTooLarge: true);
        var service = CreateService(client, new ThrowingDiagnosticWriter());

        var exception = await Assert.ThrowsAsync<StorySceneGenerationException>(() =>
            service.GenerateWithOneRepairAsync<RepairResponse>(
                [new OllamaChatMessage("user", "scene")],
                new { type = "object" },
                null,
                "Sahne 1",
                CancellationToken.None,
                OllamaOptions.DefaultTextModel,
                new OllamaFailureContext(9, 1, "SingleSceneGeneration")));

        Assert.Equal("ResponseTooLarge", exception.Stage);
        Assert.Equal(string.Empty, exception.LogPath);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task DiagnosticPayload_DoesNotAddConnectionStringOrSecretsMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorTests", Guid.NewGuid().ToString("N"));
        var diagnostics = new OllamaFailureDiagnosticWriter(root);
        var exception = new OllamaResponseTooLargeException(
            "too large",
            "{\"partial\":true}",
            new OllamaResponseMetadata { Model = OllamaOptions.DefaultTextModel });

        var path = await diagnostics.WriteAsync(
            new OllamaFailureContext(9, 1, "SingleSceneGeneration"),
            "initial",
            exception,
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("connectionString", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", json, StringComparison.OrdinalIgnoreCase);
    }

    private static StoryGenerationService CreateService(
        IOllamaClient client,
        IOllamaFailureDiagnosticWriter diagnostics) =>
        new(
            null!,
            client,
            null!,
            null!,
            diagnostics,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            NullLogger<StoryGenerationService>.Instance);

    private sealed class RepairResponse
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class RecordingDiagnosticWriter : IOllamaFailureDiagnosticWriter
    {
        public List<string> Attempts { get; } = [];

        public Task<string> WriteAsync(
            OllamaFailureContext context,
            string attemptType,
            OllamaResponseException exception,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(attemptType);
            return Task.FromResult($"C:\\diagnostics\\{attemptType}.json");
        }
    }

    private sealed class ThrowingDiagnosticWriter : IOllamaFailureDiagnosticWriter
    {
        public Task<string> WriteAsync(
            OllamaFailureContext context,
            string attemptType,
            OllamaResponseException exception,
            CancellationToken cancellationToken = default) =>
            throw new IOException("diagnostic path unavailable");
    }

    private sealed class RepairFlowOllamaClient(
        bool repairSucceeds,
        bool firstCallReturnsValue = false,
        bool firstCallTooLarge = false,
        bool firstCallTokenLimit = false,
        bool secondCallTokenLimit = false,
        bool firstCallRepetition = false) : IOllamaClient
    {
        public const string TruncatedRawMarker = "SCENE25_RAW_REPEAT_MARKER";
        public int CallCount { get; private set; }
        public List<string> Models { get; } = [];
        public List<RecordedCall> Calls { get; } = [];
        public bool SecondCallUsedDeterministicSettings { get; private set; }

        public Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OllamaHealthResult { IsAvailable = true });

        public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public async Task<TResponse> ChatStructuredAsync<TResponse>(
            IReadOnlyList<OllamaChatMessage> messages,
            object jsonSchema,
            string? modelOverride = null,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default,
            IProgress<OllamaStreamProgress>? streamProgress = null,
            OllamaGenerationSettings? generationSettings = null) =>
            (await ChatStructuredDetailedAsync<TResponse>(messages, jsonSchema, modelOverride, requestTimeout, cancellationToken, streamProgress, generationSettings)).Value;

        public Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(
            IReadOnlyList<OllamaChatMessage> messages,
            object jsonSchema,
            string? modelOverride = null,
            TimeSpan? requestTimeout = null,
            CancellationToken cancellationToken = default,
            IProgress<OllamaStreamProgress>? streamProgress = null,
            OllamaGenerationSettings? generationSettings = null)
        {
            CallCount++;
            Models.Add(modelOverride ?? string.Empty);
            Calls.Add(new RecordedCall(
                string.Join("\n", messages.Select(message => message.Content)),
                generationSettings));
            var metadata = new OllamaResponseMetadata
            {
                Model = modelOverride ?? string.Empty,
                Done = true,
                StreamCompleted = true,
                DoneReason = "stop"
            };
            if ((CallCount == 1 && !firstCallReturnsValue) || (CallCount > 1 && !repairSucceeds))
            {
                if (CallCount == 1 && firstCallTooLarge)
                {
                    throw new OllamaResponseTooLargeException("too large", "{large", metadata);
                }

                if ((CallCount == 1 && firstCallTokenLimit) || (CallCount > 1 && secondCallTokenLimit))
                {
                    metadata.DoneReason = "length";
                    metadata.ResponseTokenCount = 6144;
                    throw new OllamaResponseTruncatedException("token limit", "{" + TruncatedRawMarker + new string('x', 5000), metadata);
                }

                if (CallCount == 1 && firstCallRepetition)
                {
                    metadata.RepeatedBlockLength = 80;
                    metadata.RepeatedBlockCount = 4;
                    throw new OllamaRepetitionDetectedException("repeat", "{" + TruncatedRawMarker, metadata);
                }

                throw new OllamaStructuredResponseException(
                    "invalid",
                    "SyntaxValidation",
                    "{invalid",
                    metadata,
                    new System.Text.Json.JsonException("invalid"));
            }

            SecondCallUsedDeterministicSettings = CallCount > 1 && generationSettings is
            {
                Temperature: 0,
                TopP: 0.1,
                Think: false
            };
            var valueText = CallCount == 1 ? "invalid-domain" : "repaired";
            var value = (TResponse)(object)new RepairResponse { Value = valueText };
            return Task.FromResult(new OllamaStructuredResult<TResponse>
            {
                Value = value,
                RawResponse = $"{{\"value\":\"{valueText}\"}}",
                NormalizedJson = $"{{\"value\":\"{valueText}\"}}",
                Metadata = metadata
            });
        }

        public sealed record RecordedCall(string Prompt, OllamaGenerationSettings? Settings);
    }
}
