using Director.Enums;

namespace Director.Models;

public sealed class SceneSpeechSegment
{
    public int Id { get; set; }
    public int SceneSpeechPlanId { get; set; }
    public SceneSpeechPlan SceneSpeechPlan { get; set; } = null!;
    public SpeechSpeakerType SpeakerType { get; set; }
    public int? StoryCharacterId { get; set; }
    public StoryCharacter? StoryCharacter { get; set; }
    public string SpeakerKey { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string TurkishText { get; set; } = string.Empty;
    public string Emotion { get; set; } = string.Empty;
    public double StartTimeSeconds { get; set; }
    public double TargetDurationSeconds { get; set; }
    public double? ActualDurationSeconds { get; set; }
    public int VoiceProfileId { get; set; }
    public CharacterVoiceProfile VoiceProfile { get; set; } = null!;
    public int SortOrder { get; set; }
    public SpeechSegmentStatus Status { get; set; } = SpeechSegmentStatus.Pending;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
