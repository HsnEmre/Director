using Director.Dtos.StoryGeneration;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Director.Tests;

public sealed class StoryBibleAutonomousSmokeTests
{
    [Fact]
    public async Task OneSceneNarratorOff_UsesBriefProfileWithBoundedBudgetAndPreservesContract()
    {
        var client = new StoryBibleOllamaClient(StoryBible(characterless: true));
        var service = CreateService(client, out _);
        var project = SmallSilentProject();

        var bible = await service.GenerateStoryBibleWithCharacterRepairAsync(project, null, CancellationToken.None);

        Assert.Equal(StoryBibleOutputProfile.BriefVisual, StoryGenerationService.SelectStoryBibleOutputProfile(project));
        Assert.Empty(bible.Characters);
        Assert.Single(client.Calls);
        Assert.Equal("BriefVisual", client.Calls[0].Settings?.OutputProfile);
        Assert.Equal(1536, client.Calls[0].Settings?.NumPredict);
        Assert.True(client.Calls[0].Settings?.EstimatedPromptTokens > 0);
        Assert.True((client.Calls[0].Settings?.EstimatedPromptTokens ?? 0) + (client.Calls[0].Settings?.NumPredict ?? 0) < 32768);
        Assert.Contains("\"characters\": []", client.Calls[0].Prompt);
        Assert.Contains("Return valid JSON only", client.Calls[0].Prompt);
        Assert.Contains("No narrator, no dialogue", client.Calls[0].Prompt);
    }

    [Fact]
    public async Task StoryBibleFirstTokenLimit_FreshRetrySucceeds_UsesCleanShortRetryAndNoCharacterRepair()
    {
        var client = new StoryBibleOllamaClient(StoryBible(characterless: true), firstCallTokenLimit: true);
        var service = CreateService(client, out var diagnostics);

        var bible = await service.GenerateStoryBibleWithCharacterRepairAsync(SmallSilentProject(), null, CancellationToken.None);

        Assert.Equal("Short Rain", bible.Title);
        Assert.Equal(2, client.Calls.Count);
        Assert.Equal(["initial"], diagnostics.Attempts);
        Assert.DoesNotContain(StoryBibleOllamaClient.TruncatedRawMarker, client.Calls[1].Prompt);
        Assert.Contains("discarded", client.Calls[1].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("BriefVisual", client.Calls[1].Settings?.OutputProfile);
        Assert.Equal(2048, client.Calls[1].Settings?.NumPredict);
        Assert.DoesNotContain("Fix only the characters array", client.Calls[1].Prompt);
    }

    [Fact]
    public async Task StoryBibleTokenLimitRecoveryExhausted_ThrowsAfterFinalRegeneration()
    {
        var client = new StoryBibleOllamaClient(
            StoryBible(characterless: true),
            firstCallTokenLimit: true,
            secondCallTokenLimit: true,
            thirdCallTokenLimit: true);
        var service = CreateService(client, out var diagnostics);

        var exception = await Assert.ThrowsAsync<OllamaResponseTruncatedException>(() =>
            service.GenerateStoryBibleWithCharacterRepairAsync(SmallSilentProject(), null, CancellationToken.None));

        Assert.Contains("token limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, client.Calls.Count);
        Assert.Equal(["initial", "fresh", "final-regeneration"], diagnostics.Attempts);
        Assert.DoesNotContain("Malformed response", client.Calls[1].Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Final recovery attempt", client.Calls[2].Prompt);
    }

    [Fact]
    public void CharacterlessVisualStory_ValidationPassesWithoutCharacterRepair()
    {
        var bible = StoryBible(characterless: true);

        StoryGenerationService.ValidateStoryBible(bible);
        var issues = StoryCharacterFieldValidator.ValidateIssues(bible);

        Assert.Empty(issues);
    }

    [Fact]
    public void LargeProject_UsesDetailedProfileAndKeepsBudgetWithinContext()
    {
        var project = SmallSilentProject();
        project.CalculatedClipCount = 30;
        project.TotalDurationMinutes = 5;
        project.UseNarrator = true;
        var service = CreateService(new StoryBibleOllamaClient(StoryBible(characterless: false)), out _);

        var profile = StoryGenerationService.SelectStoryBibleOutputProfile(project);
        var budget = service.CalculateStoryBibleOutputBudget(project, profile, StoryBibleGenerationAttempt.Initial, estimatedPromptTokens: 2500);

        Assert.Equal(StoryBibleOutputProfile.Detailed, profile);
        Assert.True(budget.NumPredict >= new OllamaOptions().SceneNumPredict);
        Assert.True(budget.NumPredict <= 8192);
        Assert.True(budget.EstimatedPromptTokens + budget.NumPredict + budget.ContextMarginTokens <= budget.ContextLength);
    }

    private static StoryGenerationService CreateService(
        IOllamaClient client,
        out RecordingDiagnosticWriter diagnostics)
    {
        diagnostics = new RecordingDiagnosticWriter();
        return new StoryGenerationService(
            null!,
            client,
            new StoryPromptBuilder(),
            null!,
            diagnostics,
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            NullLogger<StoryGenerationService>.Instance);
    }

    private static FilmProject SmallSilentProject() => new()
    {
        Id = 10,
        ProjectName = "Autonomous Smoke 20260806 1730",
        Subject = "A rain-soaked empty street slowly reflecting neon light under a single umbrella caught on a bench.",
        TotalDurationMinutes = 1,
        ClipDurationSeconds = 60,
        CalculatedClipCount = 1,
        Language = "Turkish",
        TargetAudience = "General",
        StoryGenre = "Atmospheric visual short",
        VisualStyle = "Cinematic realistic nocturne",
        VideoStyle = "Slow camera drift",
        AspectRatio = "16:9",
        Resolution = "1280x720",
        UseNarrator = false,
        NarratorTone = string.Empty,
        MainCharacterDescription = string.Empty,
        AdditionalInstructions = "No human, narrator, dialogue or audio."
    };

    private static StoryBibleResponse StoryBible(bool characterless)
    {
        var response = new StoryBibleResponse
        {
            Title = "Short Rain",
            Logline = "An empty street changes under rain and light.",
            Synopsis = "A silent one-shot visual story follows rain, reflections and an abandoned umbrella as the street slowly settles.",
            OpeningSummary = "Rain reveals the empty street.",
            DevelopmentSummary = "Neon reflections stretch across puddles.",
            ClimaxSummary = "The umbrella shifts in the wind.",
            EndingSummary = "The street becomes still again.",
            WorldDescription = "A quiet neon-lit street at night after heavy rain.",
            VisualDirection = "Slow cinematic realism, wet reflections, controlled camera drift and no audio beats.",
            ContinuityRules = ["Keep the street empty.", "Preserve the umbrella.", "No audio or dialogue."]
        };

        if (!characterless)
        {
            response.Characters.Add(new StoryCharacterResponse
            {
                CharacterKey = "walker",
                Name = "Walker",
                Role = "Protagonist",
                PhysicalDescription = "A solitary adult figure seen from a distance.",
                ClothingDescription = "Dark coat and simple shoes.",
                PersonalityDescription = "Quiet and observant.",
                VoiceDescription = "No spoken voice.",
                ContinuityDescription = "Keep silhouette and clothing stable.",
                ForbiddenChanges = ["Do not change clothing."]
            });
        }

        return response;
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
            return Task.FromResult($"C:\\diagnostics\\story-bible-{attemptType}.json");
        }
    }

    private sealed class StoryBibleOllamaClient(
        StoryBibleResponse response,
        bool firstCallTokenLimit = false,
        bool secondCallTokenLimit = false,
        bool thirdCallTokenLimit = false) : IOllamaClient
    {
        public const string TruncatedRawMarker = "STORY_BIBLE_TRUNCATED_RAW_MARKER";
        public List<RecordedCall> Calls { get; } = [];

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
            Calls.Add(new RecordedCall(string.Join("\n", messages.Select(message => message.Content)), generationSettings));
            var metadata = new OllamaResponseMetadata
            {
                Model = modelOverride ?? OllamaOptions.DefaultTextModel,
                OperationName = generationSettings?.OperationName ?? string.Empty,
                OutputProfile = generationSettings?.OutputProfile,
                PromptCharacterCount = generationSettings?.PromptCharacterCount,
                EstimatedPromptTokens = generationSettings?.EstimatedPromptTokens,
                ConfiguredResponseLimit = generationSettings?.NumPredict ?? 0,
                Done = true,
                StreamCompleted = true,
                DoneReason = "stop",
                PromptTokenCount = generationSettings?.EstimatedPromptTokens ?? 0,
                ResponseTokenCount = 640,
                ResponseCharacterCount = 1800
            };

            if ((Calls.Count == 1 && firstCallTokenLimit) ||
                (Calls.Count == 2 && secondCallTokenLimit) ||
                (Calls.Count == 3 && thirdCallTokenLimit))
            {
                metadata.DoneReason = "length";
                metadata.ResponseTokenCount = generationSettings?.NumPredict ?? 3072;
                throw new OllamaResponseTruncatedException(
                    "token limit",
                    "{" + TruncatedRawMarker + new string('x', 2048),
                    metadata);
            }

            return Task.FromResult(new OllamaStructuredResult<TResponse>
            {
                Value = (TResponse)(object)response,
                RawResponse = "{\"title\":\"Short Rain\",\"characters\":[]}",
                NormalizedJson = "{\"title\":\"Short Rain\",\"characters\":[]}",
                Metadata = metadata
            });
        }

        public sealed record RecordedCall(string Prompt, OllamaGenerationSettings? Settings);
    }
}
