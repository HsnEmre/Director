using Director.Enums;

namespace Director.Dtos.Autonomous;

public sealed class AutonomousGenerationConfigurationSnapshot
{
    public int FilmProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public int TargetDurationSeconds { get; set; }
    public int TotalDurationMinutes { get; set; }
    public int ClipDurationSeconds { get; set; }
    public int CalculatedClipCount { get; set; }
    public string Language { get; set; } = string.Empty;
    public string TargetAudience { get; set; } = string.Empty;
    public string StoryGenre { get; set; } = string.Empty;
    public string VisualStyle { get; set; } = string.Empty;
    public string VideoStyle { get; set; } = string.Empty;
    public string AspectRatio { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public bool UseNarrator { get; set; }
    public string? NarratorTone { get; set; }
    public string? MainCharacterDescription { get; set; }
    public string? AdditionalInstructions { get; set; }
    public string StoryModel { get; set; } = string.Empty;
    public string ImageModelType { get; set; } = string.Empty;
    public string VideoModelType { get; set; } = string.Empty;
    public string AudioModelType { get; set; } = string.Empty;
    public int ImageInferenceSteps { get; set; } = 30;
    public int VideoInferenceSteps { get; set; } = 30;
    public int? Seed { get; set; }
    public bool RandomSeed { get; set; } = true;
    public bool GenerateAudio { get; set; } = true;
    public bool PreferLtxNativeDialogue { get; set; } = true;
    public VideoAudioGenerationMode DefaultVideoGenerationMode { get; set; } = VideoAudioGenerationMode.SilentVideo;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
