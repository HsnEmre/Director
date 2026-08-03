namespace Director.Models;

public sealed class LtxNativeVoiceProfile
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;
    public int StoryCharacterId { get; set; }
    public StoryCharacter StoryCharacter { get; set; } = null!;
    public string VoiceDescription { get; set; } = string.Empty;
    public string Language { get; set; } = "tr";
    public string SpeakingStyle { get; set; } = string.Empty;
    public string PerceivedAge { get; set; } = string.Empty;
    public string GenderPresentation { get; set; } = string.Empty;
    public string AccentDescription { get; set; } = string.Empty;
    public string PitchDescription { get; set; } = string.Empty;
    public string TempoDescription { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string SettingsHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
