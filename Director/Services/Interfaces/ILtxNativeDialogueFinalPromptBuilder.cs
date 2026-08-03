using Director.Dtos.MediaGeneration;
using Director.Models;

namespace Director.Services.Interfaces;

public interface ILtxNativeDialogueFinalPromptBuilder
{
    LtxNativeDialogueFinalPrompt Build(LtxNativeDialogueFinalPromptRequest request);
    void Validate(LtxNativeDialogueFinalPromptValidationRequest request);
}

public sealed class LtxNativeDialogueFinalPromptRequest
{
    public string VisualDirection { get; set; } = string.Empty;
    public required LtxNativeDialogueCreativeDirectionResult CreativeDirection { get; init; }
    public required StoryCharacter Speaker { get; init; }
    public required LtxNativeVoiceProfile VoiceProfile { get; init; }
    public required IReadOnlyList<SpeechDialogueLine> Dialogue { get; set; }
    public string ProjectLanguage { get; set; } = string.Empty;
    public IReadOnlyList<string> OtherCharacterDisplayNames { get; set; } = [];
}

public sealed class LtxNativeDialogueFinalPromptValidationRequest
{
    public int FilmProjectId { get; set; }
    public int SceneId { get; set; }
    public int SceneNumber { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string SpeakerDisplayName { get; set; } = string.Empty;
    public IReadOnlyList<string> ExactDialogueLines { get; set; } = [];
    public string VoiceDirection { get; set; } = string.Empty;
    public string VisualDirection { get; set; } = string.Empty;
    public IReadOnlyList<string> OtherCharacterDisplayNames { get; set; } = [];
}

public sealed class LtxNativeDialogueFinalPrompt
{
    public string CombinedPrompt { get; init; } = string.Empty;
    public string DialogueBlock { get; init; } = string.Empty;
    public string VoiceDirection { get; init; } = string.Empty;
    public string VisualDirection { get; init; } = string.Empty;
    public string SpeakerDisplayName { get; init; } = string.Empty;
    public IReadOnlyList<string> NamedSpeakerLines { get; init; } = [];
    public string OnlySpeakerLine { get; init; } = string.Empty;
}

public sealed class LtxNativeDialogueFinalPromptValidationException(IReadOnlyList<string> errors)
    : InvalidOperationException(string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
