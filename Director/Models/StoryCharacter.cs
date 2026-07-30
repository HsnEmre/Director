namespace Director.Models;

public sealed class StoryCharacter
{
    public int Id { get; set; }

    public int FilmStoryId { get; set; }
    public FilmStory FilmStory { get; set; } = null!;

    public string CharacterKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    public string PhysicalDescription { get; set; } = string.Empty;
    public string ClothingDescription { get; set; } = string.Empty;
    public string PersonalityDescription { get; set; } = string.Empty;
    public string VoiceDescription { get; set; } = string.Empty;

    public string ContinuityDescription { get; set; } = string.Empty;
    public string ForbiddenChangesJson { get; set; } = "[]";

    public int SortOrder { get; set; }
}
