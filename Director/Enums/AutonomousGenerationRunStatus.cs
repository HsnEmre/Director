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
    Paused = 12
}
