namespace Director.Enums;

public enum AutonomousGenerationRunStatus
{
    Pending = 0,
    Validating = 1,
    GeneratingStory = 2,
    GeneratingScenes = 3,
    GeneratingImages = 4,
    GeneratingVideos = 5,
    GeneratingAudio = 6,
    Finalizing = 7,
    Completed = 8,
    Failed = 9,
    CancelRequested = 10,
    Cancelled = 11,
    Paused = 12,
    GeneratingStoryNarrative = 13,
    GeneratingCharacters = 14,
    GeneratingNarrativeScenes = 15,
    GeneratingImagePrompts = 16,
    GeneratingVideoPrompts = 17
}
