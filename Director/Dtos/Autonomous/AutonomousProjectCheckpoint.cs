namespace Director.Dtos.Autonomous;

public sealed class AutonomousProjectCheckpoint
{
    public int FilmProjectId { get; set; }
    public int ExpectedSceneCount { get; set; }
    public int SceneCount { get; set; }
    public bool HasValidStory { get; set; }
    public bool HasValidCharacters { get; set; }
    public int? FirstMissingNarrativeSceneNumber { get; set; }
    public int? FirstMissingImagePromptSceneNumber { get; set; }
    public int? FirstMissingVideoPromptSceneNumber { get; set; }
    public int? FirstMissingSelectedImageSceneNumber { get; set; }
    public int? FirstMissingSelectedVideoSceneNumber { get; set; }
    public int? FirstMissingSceneAudioSceneNumber { get; set; }
}
