using Director.WanGp;

namespace Director.Services.Interfaces;

public interface ILtxNativeDialoguePromptComposer
{
    Task<LtxNativeDialoguePromptResult> BuildAsync(int sceneId, int referenceImageAssetId, CancellationToken cancellationToken = default);
}

public sealed class LtxNativeDialoguePromptResult
{
    public bool HasDialogue { get; set; }
    public string VideoPrompt { get; set; } = string.Empty;
    public string AudioDialoguePrompt { get; set; } = string.Empty;
    public string CombinedPrompt { get; set; } = string.Empty;
    public string SpeakerKey { get; set; } = string.Empty;
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
}
