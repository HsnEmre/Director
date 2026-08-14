using Director.Dtos.StoryGeneration;
using Director.Data;
using Director.Models;
using Director.Options;
using Director.Services;
using Microsoft.EntityFrameworkCore;

namespace Director.Tests;

public sealed class StoryTextGenerationPolicyTests
{
    [Fact]
    public void AllTextModels_DefaultToQwen30B()
    {
        var options = new OllamaOptions();
        var models = new[]
        {
            options.Model,
            options.StoryTextModel,
            options.SceneTextModel,
            options.PromptPreparationModel,
            options.DialogueModel,
            options.VisualPromptModel,
            options.VideoPromptModel
        };

        Assert.All(models, model => Assert.Equal(OllamaOptions.DefaultTextModel, model));
        Assert.DoesNotContain(models, model => model.Contains("4b", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, options.SceneBatchSize);
    }

    [Fact]
    public void FourBOverride_IsRejectedWithoutFallback()
    {
        var options = new OllamaOptions { SceneTextModel = "qwen3:4b-instruct" };

        var result = new OllamaOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(nameof(options.SceneTextModel), result.FailureMessage);
    }

    [Fact]
    public void SingleScenePrompt_ContainsOnlyRequestedSceneContext()
    {
        var project = new FilmProject
        {
            Subject = "Mete Han'in yolculugu",
            CalculatedClipCount = 30,
            ClipDurationSeconds = 10,
            VisualStyle = "cinematic historical realism",
            VideoStyle = "controlled camera movement",
            Language = "Turkce",
            AdditionalInstructions = new string('x', 5000)
        };
        var story = new FilmStory
        {
            Title = "Mete Han",
            Synopsis = "A young leader finds his path.",
            OpeningSummary = "The journey begins.",
            DevelopmentSummary = "The conflict grows.",
            ClimaxSummary = "The decisive confrontation.",
            EndingSummary = "The new order is established."
        };
        story.Characters.Add(new StoryCharacter
        {
            CharacterKey = "metehan",
            Name = "Mete Han",
            Role = "Protagonist",
            PhysicalDescription = "young warrior",
            ClothingDescription = "steppe armor",
            ContinuityDescription = "same armor and hairstyle"
        });

        var prompt = new StoryPromptBuilder().BuildSingleScenePackageUserPrompt(project, story, 2, "Scene 1 ending.");

        Assert.Contains("Create only scene 2 of 30", prompt);
        Assert.Contains("Relevant story section: The journey begins.", prompt);
        Assert.Contains("Target duration seconds: 10", prompt);
        Assert.Contains("Scene 1 ending.", prompt);
        Assert.DoesNotContain(project.AdditionalInstructions, prompt);
        Assert.DoesNotContain("scene 3", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleSceneSchema_HasNoScenesBatchArray()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(StoryJsonSchemas.SingleScenePackageSchema());

        Assert.Contains("sceneNumber", json);
        Assert.Contains("dialogueJson", json);
        Assert.DoesNotContain("\"scenes\"", json);
        Assert.DoesNotContain("maxLength", json);
    }

    [Fact]
    public void NarrativeSceneSchema_DoesNotContainMediaPromptFields()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(StoryJsonSchemas.NarrativeSceneSchema());

        Assert.Contains("dialogueIntent", json);
        Assert.DoesNotContain("imagePrompt", json);
        Assert.DoesNotContain("videoPrompt", json);
        Assert.DoesNotContain("dialogueJson", json);
        Assert.DoesNotContain("narrationText", json);
    }

    [Fact]
    public void ImageAndVideoPromptSchemas_AreSeparate()
    {
        var imageJson = System.Text.Json.JsonSerializer.Serialize(StoryJsonSchemas.SceneImagePromptSchema());
        var videoJson = System.Text.Json.JsonSerializer.Serialize(StoryJsonSchemas.SceneVideoPromptSchema());

        Assert.Contains("imagePrompt", imageJson);
        Assert.DoesNotContain("videoPrompt", imageJson);
        Assert.Contains("videoPrompt", videoJson);
        Assert.DoesNotContain("imagePrompt", videoJson);
        Assert.Contains("motionPlan", videoJson);
    }

    [Fact]
    public void PromptTokenEstimate_IsStableAndNonZero()
    {
        Assert.Equal(3, StoryGenerationService.EstimatePromptTokens("123456789"));
    }

    [Theory]
    [InlineData("ses")]
    [InlineData("rüzgarın sesi")]
    [InlineData("müzik")]
    [InlineData("konuşma")]
    [InlineData("diyalog")]
    [InlineData("soft narration begins")]
    [InlineData("sound effects")]
    [InlineData("voice")]
    [InlineData("spoken words")]
    public void SilentVideoValidator_RejectsRealAudioInstructions(string prompt)
    {
        Assert.True(StoryGenerationService.TryFindForbiddenSilentVideoInstruction(prompt, isNegativePrompt: false, out _));
    }

    [Theory]
    [InlineData("sessiz")]
    [InlineData("sessizce yürür")]
    [InlineData("tohum sessizce parlar")]
    public void SilentVideoValidator_AllowsSilentCompatibleTurkishWords(string prompt)
    {
        Assert.False(StoryGenerationService.TryFindForbiddenSilentVideoInstruction(prompt, isNegativePrompt: false, out _));
    }

    [Fact]
    public void SilentVideoValidator_AllowsNegativePromptAudioBlockers()
    {
        Assert.False(StoryGenerationService.TryFindForbiddenSilentVideoInstruction(
            "no sound, no music, no dialogue, silent, without audio",
            isNegativePrompt: true,
            out _));
    }

    [Fact]
    public void VideoPromptCheckpointQuery_IsSqlTranslatableAndProjectScoped()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=DirectorCheckpointTranslationTest;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        using var db = new AppDbContext(options);

        var query = StoryGenerationService.BuildVideoPromptCheckpointQuery(db.FilmScenes.AsNoTracking(), 16);
        var sql = query.ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FilmProjectId", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(StoryGenerationService.HasInvalidSilentVideoPromptFields), sql);
        Assert.DoesNotContain("Title", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SceneDescription", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VideoPromptCheckpointSelector_FindsEmptyVideoPromptInTargetProject()
    {
        var scene = StoryGenerationService.FindFirstMissingOrInvalidVideoPromptCheckpoint(
            [
                CheckpointScene(16, 1, videoPrompt: "", videoNegativePrompt: "no sound"),
                CheckpointScene(17, 1, videoPrompt: "", videoNegativePrompt: "")
            ],
            filmProjectId: 16);

        Assert.NotNull(scene);
        Assert.Equal(1, scene!.SceneNumber);
        Assert.Equal(16, scene.FilmProjectId);
    }

    [Fact]
    public void VideoPromptCheckpointSelector_FindsEmptyVideoNegativePromptInTargetProject()
    {
        var scene = StoryGenerationService.FindFirstMissingOrInvalidVideoPromptCheckpoint(
            [
                CheckpointScene(16, 1, videoPrompt: "visual movement", videoNegativePrompt: ""),
                CheckpointScene(17, 1, videoPrompt: "", videoNegativePrompt: "")
            ],
            filmProjectId: 16);

        Assert.NotNull(scene);
        Assert.Equal(1, scene!.SceneNumber);
    }

    [Fact]
    public void VideoPromptCheckpointSelector_FindsInvalidSilentPromptClientSide()
    {
        var scene = StoryGenerationService.FindFirstMissingOrInvalidVideoPromptCheckpoint(
            [
                CheckpointScene(16, 1, videoPrompt: "soft narration begins", videoNegativePrompt: "no sound"),
                CheckpointScene(16, 2, videoPrompt: "", videoNegativePrompt: "")
            ],
            filmProjectId: 16);

        Assert.NotNull(scene);
        Assert.Equal(1, scene!.SceneNumber);
    }

    [Fact]
    public void VideoPromptCheckpointSelector_ValidPromptsAreNotMissingOrInvalid()
    {
        var scene = StoryGenerationService.FindFirstMissingOrInvalidVideoPromptCheckpoint(
            [
                CheckpointScene(16, 1, videoPrompt: "the figure moves silently through the frame", videoNegativePrompt: "no sound, no music, no dialogue")
            ],
            filmProjectId: 16);

        Assert.Null(scene);
    }

    [Theory]
    [InlineData("", "no sound")]
    [InlineData("visual movement", "")]
    [InlineData("soft narration begins", "no sound")]
    public void VideoPromptCheckpointSelector_IgnoresOtherProjectMissingOrInvalidPrompts(string otherVideoPrompt, string otherNegativePrompt)
    {
        var scene = StoryGenerationService.FindFirstMissingOrInvalidVideoPromptCheckpoint(
            [
                CheckpointScene(16, 1, videoPrompt: "valid silent camera move", videoNegativePrompt: "no sound, no music, no dialogue"),
                CheckpointScene(17, 1, videoPrompt: otherVideoPrompt, videoNegativePrompt: otherNegativePrompt)
            ],
            filmProjectId: 16);

        Assert.Null(scene);
    }

    private static StoryGenerationService.VideoPromptCheckpointScene CheckpointScene(
        int filmProjectId,
        int sceneNumber,
        string videoPrompt,
        string videoNegativePrompt) =>
        new()
        {
            Id = (filmProjectId * 100) + sceneNumber,
            FilmProjectId = filmProjectId,
            SceneNumber = sceneNumber,
            VideoPrompt = videoPrompt,
            VideoNegativePrompt = videoNegativePrompt
        };
}
