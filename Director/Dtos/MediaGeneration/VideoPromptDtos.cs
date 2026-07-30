namespace Director.Dtos.MediaGeneration;

public sealed class VideoPromptCompositionRequest
{
    public int FilmProjectId { get; set; }
    public int SceneId { get; set; }
    public int SceneNumber { get; set; }
    public string ReferenceImagePath { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string StoryTitle { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string VisualDirection { get; set; } = string.Empty;
    public string WorldDescription { get; set; } = string.Empty;
    public string StoryGenre { get; set; } = string.Empty;
    public string VisualStyle { get; set; } = string.Empty;
    public string VideoStyle { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string AspectRatio { get; set; } = string.Empty;
    public int ClipDurationSeconds { get; set; }
    public string SceneTitle { get; set; } = string.Empty;
    public string StoryBeat { get; set; } = string.Empty;
    public string SceneDescription { get; set; } = string.Empty;
    public string ExistingVideoPrompt { get; set; } = string.Empty;
    public string ExistingVideoNegativePrompt { get; set; } = string.Empty;
    public string ContinuityFromPreviousScene { get; set; } = string.Empty;
    public string PreviousSceneTitle { get; set; } = string.Empty;
    public string PreviousSceneStoryBeat { get; set; } = string.Empty;
    public string PreviousSceneEndingContext { get; set; } = string.Empty;
    public string NextSceneTitle { get; set; } = string.Empty;
    public string NextSceneStoryBeat { get; set; } = string.Empty;
    public string Characters { get; set; } = string.Empty;
    public string LocationDescription { get; set; } = string.Empty;
    public string TimeOfDay { get; set; } = string.Empty;
}

public sealed class VideoPromptCompositionResult
{
    public string VideoPrompt { get; set; } = string.Empty;
    public string VideoNegativePrompt { get; set; } = string.Empty;
    public string MotionSummary { get; set; } = string.Empty;
    public List<string> SubjectActions { get; set; } = [];
    public string CameraMovement { get; set; } = string.Empty;
    public List<string> EnvironmentMotion { get; set; } = [];
    public string StartState { get; set; } = string.Empty;
    public string EndState { get; set; } = string.Empty;
    public List<string> ContinuityPreserved { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class VideoPromptContinuityContext
{
    public string PreviousSceneEndingContext { get; set; } = string.Empty;
    public string NextSceneStoryBeat { get; set; } = string.Empty;
}
