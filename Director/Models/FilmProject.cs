using Director.Enums;

namespace Director.Models;

public class FilmProject
{
    public int Id { get; set; }

    public string ProjectName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;

    public int TotalDurationMinutes { get; set; }
    public int ClipDurationSeconds { get; set; }
    public int CalculatedClipCount { get; set; }

    public string Language { get; set; } = "Türkçe";
    public string TargetAudience { get; set; } = "Genel İzleyici";
    public string StoryGenre { get; set; } = string.Empty;

    public string VisualStyle { get; set; } = string.Empty;
    public string VideoStyle { get; set; } = string.Empty;

    public string AspectRatio { get; set; } = "16:9";
    public string Resolution { get; set; } = "1920x1080";

    public bool UseNarrator { get; set; }
    public string? NarratorTone { get; set; }

    public string? MainCharacterDescription { get; set; }
    public string? AdditionalInstructions { get; set; }

    public FilmProjectStatus Status { get; set; } = FilmProjectStatus.Draft;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public FilmStory? Story { get; set; }
    public ICollection<FilmScene> Scenes { get; set; } = new List<FilmScene>();
    public ICollection<GenerationJob> GenerationJobs { get; set; } = new List<GenerationJob>();
    public ICollection<SceneMediaAsset> MediaAssets { get; set; } = new List<SceneMediaAsset>();
}
