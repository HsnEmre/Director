using Director.Enums;

namespace Director.Models;

public sealed class CharacterVoiceProfile
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;
    public int? StoryCharacterId { get; set; }
    public StoryCharacter? StoryCharacter { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public GenerationProvider Provider { get; set; } = GenerationProvider.WanGp;
    public string ModelType { get; set; } = string.Empty;
    public string VoicePresetKey { get; set; } = string.Empty;
    public string VoicePresetDisplayName { get; set; } = string.Empty;
    public string Language { get; set; } = "tr";
    public double SpeakingRate { get; set; } = 1;
    public string EmotionStyle { get; set; } = string.Empty;
    public double? CfgScale { get; set; }
    public int? Seed { get; set; }
    public bool DoSample { get; set; }
    public double? Temperature { get; set; }
    public int? MaxNewTokens { get; set; }
    public bool IsLocked { get; set; }
    public bool UseEmotionStyling { get; set; }
    public string SettingsHash { get; set; } = string.Empty;
    public bool IsNarrator { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
