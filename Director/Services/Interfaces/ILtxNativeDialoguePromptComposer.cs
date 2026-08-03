using Director.WanGp;

namespace Director.Services.Interfaces;

public interface ILtxNativeDialoguePromptComposer
{
    Task<LtxNativeDialoguePromptResult> BuildAsync(int sceneId, int referenceImageAssetId, CancellationToken cancellationToken = default);
    Task<LtxNativeDialoguePromptResult> BuildReadOnlyAsync(int sceneId, int referenceImageAssetId, bool allowRepair = false, CancellationToken cancellationToken = default);
}

public sealed class LtxNativeDialoguePromptResult
{
    public int FilmProjectId { get; set; }
    public int SceneId { get; set; }
    public int SceneNumber { get; set; }
    public bool HasDialogue { get; set; }
    public string VideoPrompt { get; set; } = string.Empty;
    public string AudioDialoguePrompt { get; set; } = string.Empty;
    public string CombinedPrompt { get; set; } = string.Empty;
    public string SpeakerKey { get; set; } = string.Empty;
    public string SpeakerDisplayName { get; set; } = string.Empty;
    public string ExactDialogue { get; set; } = string.Empty;
    public int SpeakerCount { get; set; }
    public int DialogueCount { get; set; }
    public double EstimatedSpeechDurationSeconds { get; set; }
    public List<string> Warnings { get; set; } = [];
    public bool IsValid { get; set; }
    public string DialogueSourceHash { get; set; } = string.Empty;
    public List<string> ExactSpokenLines { get; set; } = [];
    public List<int> CharacterVoiceProfileIds { get; set; } = [];
    public List<string> VoiceSettingsHashes { get; set; } = [];
    public string VoiceProfileSource { get; set; } = string.Empty;
    public string VoiceDirection { get; set; } = string.Empty;
    public List<string> NamedSpeakerCanonicalLines { get; set; } = [];
    public string OnlySpeakerCanonicalLine { get; set; } = string.Empty;
    public List<string> OtherCharacterDisplayNames { get; set; } = [];
    public bool ModelReturnedCombinedPrompt { get; set; }
    public string Model { get; set; } = string.Empty;
    public int PromptTokenCount { get; set; }
    public int ResponseTokenCount { get; set; }
    public int ResponseCharacterCount { get; set; }
    public bool Done { get; set; }
    public string DoneReason { get; set; } = string.Empty;
    public string RawResponseShape { get; set; } = string.Empty;
    public string ParseStage { get; set; } = string.Empty;
    public string ValidationResult { get; set; } = string.Empty;
    public bool RepairUsed { get; set; }
    public string DiagnosticPath { get; set; } = string.Empty;
    public string DiagnosticCorrelationId { get; set; } = string.Empty;
}
