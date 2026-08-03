using Director.Enums;
using System.Text.Json.Serialization;

namespace Director.Dtos.StoryGeneration;

public sealed class StoryBibleResponse
{
    public string Title { get; set; } = string.Empty;
    public string Logline { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string OpeningSummary { get; set; } = string.Empty;
    public string DevelopmentSummary { get; set; } = string.Empty;
    public string ClimaxSummary { get; set; } = string.Empty;
    public string EndingSummary { get; set; } = string.Empty;
    public string WorldDescription { get; set; } = string.Empty;
    public string VisualDirection { get; set; } = string.Empty;
    public List<string> ContinuityRules { get; set; } = new();
    public List<StoryCharacterResponse> Characters { get; set; } = new();
}

public sealed class StoryCharacterResponse
{
    public string CharacterKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PhysicalDescription { get; set; } = string.Empty;
    public string ClothingDescription { get; set; } = string.Empty;
    public string PersonalityDescription { get; set; } = string.Empty;
    public string VoiceDescription { get; set; } = string.Empty;
    public string ContinuityDescription { get; set; } = string.Empty;
    public List<string> ForbiddenChanges { get; set; } = new();
}

public sealed class SceneOutlineBatchResponse
{
    public List<SceneOutlineItemResponse> Scenes { get; set; } = new();
}

public sealed class SceneOutlineItemResponse
{
    public int SceneNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string StoryBeat { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public List<string> Characters { get; set; } = new();
    public string Location { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;
}

public sealed class ScenePackageBatchResponse
{
    public List<ScenePackageItemResponse> Scenes { get; set; } = new();
}

public sealed class SingleScenePackageResponse
{
    [JsonRequired]
    public int SceneNumber { get; set; }
    [JsonRequired]
    public int DurationSeconds { get; set; }
    [JsonRequired]
    public string Title { get; set; } = string.Empty;
    [JsonRequired]
    public string StoryBeat { get; set; } = string.Empty;
    [JsonRequired]
    public string SceneDescription { get; set; } = string.Empty;
    [JsonRequired]
    public string LocationDescription { get; set; } = string.Empty;
    [JsonRequired]
    public string TimeOfDay { get; set; } = string.Empty;
    [JsonRequired]
    public List<string> Characters { get; set; } = new();
    [JsonRequired]
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;
    [JsonRequired]
    public string ImagePrompt { get; set; } = string.Empty;
    [JsonRequired]
    public string ImageNegativePrompt { get; set; } = string.Empty;
    [JsonRequired]
    public string VideoPrompt { get; set; } = string.Empty;
    [JsonRequired]
    public string VideoNegativePrompt { get; set; } = string.Empty;
    [JsonRequired]
    public string NarrationText { get; set; } = string.Empty;
    [JsonRequired]
    public string DialogueJson { get; set; } = string.Empty;
    [JsonRequired]
    public List<string> ValidationChecklist { get; set; } = new();
}

public sealed class ScenePackageItemResponse
{
    public int SceneNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string StoryBeat { get; set; } = string.Empty;
    public string SceneDescription { get; set; } = string.Empty;
    public string LocationDescription { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
    public List<string> Characters { get; set; } = new();
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;
    public string ImagePrompt { get; set; } = string.Empty;
    public string ImageNegativePrompt { get; set; } = string.Empty;
    public string VideoPrompt { get; set; } = string.Empty;
    public string VideoNegativePrompt { get; set; } = string.Empty;
    public string NarrationText { get; set; } = string.Empty;
    public List<DialogueLineResponse> Dialogue { get; set; } = new();
    public List<string> ValidationChecklist { get; set; } = new();
}

public sealed class DialogueLineResponse
{
    public string CharacterKey { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class SceneOutlineItemDto
{
    public int SceneNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string StoryBeat { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public IReadOnlyList<string> Characters { get; set; } = Array.Empty<string>();
    public string Location { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;
}

public sealed class StoryGenerationProgress
{
    public string Phase { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int CompletedItems { get; set; }
    public int TotalItems { get; set; }
    public double Percentage { get; set; }
    public GenerationLogLevel Level { get; set; }
    public DateTime Timestamp { get; set; }
    public int? SceneStart { get; set; }
    public int? SceneEnd { get; set; }
}

public sealed class StoryGenerationProgressResult
{
    public int FilmProjectId { get; set; }
    public int FilmStoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int GeneratedSceneCount { get; set; }
}
