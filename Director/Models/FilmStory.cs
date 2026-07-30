namespace Director.Models;

public sealed class FilmStory
{
    public int Id { get; set; }

    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;

    public string Title { get; set; } = string.Empty;
    public string Logline { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;

    public string OpeningSummary { get; set; } = string.Empty;
    public string DevelopmentSummary { get; set; } = string.Empty;
    public string ClimaxSummary { get; set; } = string.Empty;
    public string EndingSummary { get; set; } = string.Empty;

    public string WorldDescription { get; set; } = string.Empty;
    public string VisualDirection { get; set; } = string.Empty;
    public string ContinuityRulesJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<StoryCharacter> Characters { get; set; } = new List<StoryCharacter>();
    public ICollection<FilmScene> Scenes { get; set; } = new List<FilmScene>();
}
