using System.Reflection;
using Director.Dtos.StoryGeneration;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class SingleSceneContinuityValidationTests
{
    [Fact]
    public void SceneOne_EmptyContinuity_IsNormalizedToOpeningCanonicalValue()
    {
        var response = ValidScene(sceneNumber: 1, continuity: string.Empty);

        ValidateSingleScene(response, expectedSceneNumber: 1);

        Assert.Equal(StoryGenerationService.OpeningSceneContinuityFromPreviousScene, response.ContinuityFromPreviousScene);
    }

    [Fact]
    public void SceneOne_NullContinuity_IsNormalizedToOpeningCanonicalValue()
    {
        var response = ValidScene(sceneNumber: 1, continuity: null!);

        ValidateSingleScene(response, expectedSceneNumber: 1);

        Assert.Equal(StoryGenerationService.OpeningSceneContinuityFromPreviousScene, response.ContinuityFromPreviousScene);
    }

    [Fact]
    public void SceneTwo_EmptyContinuity_FailsValidation()
    {
        var response = ValidScene(sceneNumber: 2, continuity: " ");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ValidateSingleScene(response, expectedSceneNumber: 2));

        Assert.Contains("continuityFromPreviousScene is empty", exception.Message);
    }

    [Fact]
    public void SceneTwo_ConcreteContinuity_PassesValidation()
    {
        var response = ValidScene(sceneNumber: 2, continuity: "The camera continues from the rain-soaked street corner established in scene 1.");

        ValidateSingleScene(response, expectedSceneNumber: 2);

        Assert.Equal("The camera continues from the rain-soaked street corner established in scene 1.", response.ContinuityFromPreviousScene);
    }

    [Fact]
    public void SceneOnePromptContract_RequestsOpeningCanonicalContinuity()
    {
        var prompt = new StoryPromptBuilder().BuildSingleScenePackageUserPrompt(
            SmallProject(),
            SmallStory(),
            sceneNumber: 1,
            previousSceneContext: string.Empty);

        Assert.Contains("continuityFromPreviousScene", prompt);
        Assert.Contains(StoryGenerationService.OpeningSceneContinuityFromPreviousScene, prompt);
        Assert.Contains("exactly", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SceneOneOnlyEmptyContinuity_DoesNotCallModelRepair()
    {
        var response = ValidScene(sceneNumber: 1, continuity: string.Empty);
        var client = new SingleSceneOllamaClient(response);
        var diagnostics = new RecordingDiagnosticWriter();
        var service = CreateService(client, diagnostics);

        var result = await service.GenerateWithOneRepairAsync<SingleScenePackageResponse>(
            [new OllamaChatMessage("user", "scene 1")],
            new { type = "object" },
            null,
            "Sahne 1",
            CancellationToken.None,
            OllamaOptions.DefaultTextModel,
            new OllamaFailureContext(11, 1, "SingleSceneGeneration"),
            candidate => ValidateSingleScene(candidate, expectedSceneNumber: 1));

        Assert.Equal(StoryGenerationService.OpeningSceneContinuityFromPreviousScene, result.ContinuityFromPreviousScene);
        Assert.Equal(1, client.CallCount);
        Assert.Empty(diagnostics.Attempts);
    }

    [Fact]
    public async Task SceneOneRepairPrompt_IncludesOpeningCanonicalContinuityRule()
    {
        var initial = ValidScene(sceneNumber: 1, continuity: "bad");
        initial.Title = string.Empty;
        var repaired = ValidScene(sceneNumber: 1, continuity: StoryGenerationService.OpeningSceneContinuityFromPreviousScene);
        var client = new SingleSceneOllamaClient(initial, repaired);
        var service = CreateService(client, new RecordingDiagnosticWriter());

        _ = await service.GenerateWithOneRepairAsync<SingleScenePackageResponse>(
            [new OllamaChatMessage("user", "scene 1")],
            new { type = "object" },
            null,
            "Sahne 1",
            CancellationToken.None,
            OllamaOptions.DefaultTextModel,
            new OllamaFailureContext(11, 1, "SingleSceneGeneration"),
            candidate => ValidateSingleScene(candidate, expectedSceneNumber: 1));

        Assert.Equal(2, client.CallCount);
        Assert.Contains(StoryGenerationService.OpeningSceneContinuityFromPreviousScene, client.Prompts[1]);
        Assert.Contains("scene 1", client.Prompts[1], StringComparison.OrdinalIgnoreCase);
    }

    private static StoryGenerationService CreateService(
        IOllamaClient client,
        IOllamaFailureDiagnosticWriter diagnostics) =>
        new(
            null!,
            client,
            new StoryPromptBuilder(),
            null!,
            diagnostics,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            NullLogger<StoryGenerationService>.Instance);

    private static void ValidateSingleScene(SingleScenePackageResponse response, int expectedSceneNumber)
    {
        var method = typeof(StoryGenerationService).GetMethod(
            "ValidateSingleSceneResponse",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(StoryGenerationService), "ValidateSingleSceneResponse");

        try
        {
            method.Invoke(null, [response, expectedSceneNumber, 10]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException inner)
        {
            throw inner;
        }
    }

    private static SingleScenePackageResponse ValidScene(int sceneNumber, string? continuity) => new()
    {
        SceneNumber = sceneNumber,
        DurationSeconds = 10,
        Title = "Rain Street",
        StoryBeat = "Rain gathers around the red umbrella.",
        SceneDescription = "A red umbrella waits under a yellow street lamp on wet pavement.",
        LocationDescription = "Empty rainy city street under a yellow lamp.",
        TimeOfDay = "Night",
        Characters = [],
        ContinuityFromPreviousScene = continuity!,
        ImagePrompt = "Cinematic realistic red umbrella under yellow street lamp on a rainy empty street.",
        ImageNegativePrompt = "people, text, watermark",
        VideoPrompt = "Rain falls slowly as the camera drifts toward the red umbrella under the yellow lamp.",
        VideoNegativePrompt = "people, text, scene transition",
        NarrationText = string.Empty,
        DialogueJson = "[]",
        ValidationChecklist = ["sceneNumber is correct"]
    };

    private static FilmProject SmallProject() => new()
    {
        ProjectName = "Smoke",
        Subject = "Red umbrella in rain.",
        CalculatedClipCount = 1,
        ClipDurationSeconds = 10,
        Language = "Turkish",
        VisualStyle = "Cinematic",
        VideoStyle = "Slow",
        Resolution = "1280x720"
    };

    private static FilmStory SmallStory() => new()
    {
        Title = "Rain",
        Synopsis = "A quiet rain scene.",
        OpeningSummary = "The umbrella appears under rain.",
        DevelopmentSummary = "Rain continues.",
        ClimaxSummary = "Light intensifies.",
        EndingSummary = "The street settles.",
        WorldDescription = "A wet empty street.",
        VisualDirection = "Quiet cinematic realism.",
        ContinuityRulesJson = "[]",
        Characters = []
    };

    private sealed class SingleSceneOllamaClient(params SingleScenePackageResponse[] responses) : IOllamaClient
    {
        public int CallCount { get; private set; }
        public List<string> Prompts { get; } = [];

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
            Prompts.Add(string.Join("\n", messages.Select(message => message.Content)));
            var index = Math.Min(CallCount, responses.Length - 1);
            CallCount++;
            return Task.FromResult(new OllamaStructuredResult<TResponse>
            {
                Value = (TResponse)(object)responses[index],
                RawResponse = "{\"sceneNumber\":1}",
                NormalizedJson = "{\"sceneNumber\":1}",
                Metadata = new OllamaResponseMetadata
                {
                    Model = modelOverride ?? OllamaOptions.DefaultTextModel,
                    Done = true,
                    StreamCompleted = true,
                    DoneReason = "stop",
                    ResponseCharacterCount = 1000
                }
            });
        }
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
            return Task.FromResult($"C:\\diagnostics\\single-scene-{attemptType}.json");
        }
    }
}
