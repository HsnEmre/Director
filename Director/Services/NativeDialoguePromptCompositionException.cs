namespace Director.Services;

public enum NativeDialoguePromptFailureStage
{
    SceneInputValidation,
    DialogueJsonParsing,
    SpeakerResolution,
    VoiceProfileLookup,
    VoiceProfileGeneration,
    OllamaTransport,
    OllamaResponseParsing,
    ResponseValidation,
    PromptAssembly,
    WanGpCompatibilityValidation
}

public sealed class NativeDialoguePromptCompositionException : InvalidOperationException
{
    public NativeDialoguePromptCompositionException(
        int filmProjectId,
        int sceneId,
        int sceneNumber,
        NativeDialoguePromptFailureStage failureStage,
        string safeReason,
        string diagnosticPath = "",
        string? characterKey = null,
        Exception? innerException = null)
        : base(safeReason, innerException)
    {
        FilmProjectId = filmProjectId;
        SceneId = sceneId;
        SceneNumber = sceneNumber;
        FailureStage = failureStage;
        SafeReason = safeReason;
        DiagnosticPath = diagnosticPath;
        CharacterKey = characterKey;
    }

    public int FilmProjectId { get; }
    public int SceneId { get; }
    public int SceneNumber { get; }
    public string? CharacterKey { get; }
    public NativeDialoguePromptFailureStage FailureStage { get; }
    public string SafeReason { get; }
    public string DiagnosticPath { get; }
}
