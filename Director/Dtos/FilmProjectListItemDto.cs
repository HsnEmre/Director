using Director.Enums;

namespace Director.Dtos;

public sealed class FilmProjectListItemDto
{
    public int Id { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string SubjectPreview { get; set; } = string.Empty;
    public int TotalDurationMinutes { get; set; }
    public int ClipDurationSeconds { get; set; }
    public int CalculatedClipCount { get; set; }
    public string StoryGenre { get; set; } = string.Empty;
    public string VisualStyle { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public FilmProjectStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool HasStory { get; set; }
    public int GeneratedSceneCount { get; set; }
}
