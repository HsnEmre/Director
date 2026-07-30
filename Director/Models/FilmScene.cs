using Director.Enums;

namespace Director.Models;

public sealed class FilmScene
{
    public int Id { get; set; }

    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;

    public int FilmStoryId { get; set; }
    public FilmStory FilmStory { get; set; } = null!;

    public int SceneNumber { get; set; }
    public int DurationSeconds { get; set; }

    public string Title { get; set; } = string.Empty;
    public string StoryBeat { get; set; } = string.Empty;
    public string SceneDescription { get; set; } = string.Empty;

    public string LocationDescription { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;

    public string CharactersJson { get; set; } = "[]";
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;

    public string ImagePrompt { get; set; } = string.Empty;
    public string ImageNegativePrompt { get; set; } = string.Empty;

    public string VideoPrompt { get; set; } = string.Empty;
    public string VideoNegativePrompt { get; set; } = string.Empty;

    public string NarrationText { get; set; } = string.Empty;
    public string DialogueJson { get; set; } = "[]";

    public string ValidationChecklistJson { get; set; } = "[]";

    public FilmSceneStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<GenerationJob> GenerationJobs { get; set; } = new List<GenerationJob>();
    public ICollection<SceneMediaAsset> MediaAssets { get; set; } = new List<SceneMediaAsset>();
}
