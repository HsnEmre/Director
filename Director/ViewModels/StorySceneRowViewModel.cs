using Director.Enums;

namespace Director.ViewModels;

public sealed class StorySceneRowViewModel
{
    public int SceneNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public string StoryBeat { get; set; } = string.Empty;
    public string ImagePrompt { get; set; } = string.Empty;
    public string ImageNegativePrompt { get; set; } = string.Empty;
    public string VideoPrompt { get; set; } = string.Empty;
    public string VideoNegativePrompt { get; set; } = string.Empty;
    public string NarrationText { get; set; } = string.Empty;
    public string DialogueJson { get; set; } = string.Empty;
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;
    public string ValidationChecklistJson { get; set; } = string.Empty;
    public FilmSceneStatus Status { get; set; }
}
