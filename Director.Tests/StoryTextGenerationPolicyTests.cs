using Director.Dtos.StoryGeneration;
using Director.Models;
using Director.Options;
using Director.Services;

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
    public void PromptTokenEstimate_IsStableAndNonZero()
    {
        Assert.Equal(3, StoryGenerationService.EstimatePromptTokens("123456789"));
    }
}
