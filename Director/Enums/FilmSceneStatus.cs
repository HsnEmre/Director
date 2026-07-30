namespace Director.Enums;

public enum FilmSceneStatus
{
    Planned = 0,
    PromptReady = 1,
    ImageGenerating = 2,
    ImageValidationPending = 3,
    ImageApproved = 4,
    ImageRejected = 5,
    VideoGenerating = 6,
    VideoCompleted = 7,
    Failed = 8
}
