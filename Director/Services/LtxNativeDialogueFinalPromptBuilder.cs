using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Director.Services.Interfaces;

namespace Director.Services;

public sealed class LtxNativeDialogueFinalPromptBuilder : ILtxNativeDialogueFinalPromptBuilder
{
    public const int MaxPromptCharacters = 8_000;
    private static readonly JsonSerializerOptions QuotedTextOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public LtxNativeDialogueFinalPrompt Build(LtxNativeDialogueFinalPromptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Dialogue.Count == 0)
        {
            throw new LtxNativeDialogueFinalPromptValidationException(["Authoritative dialogue is required."]);
        }

        var speakerName = EscapeInline(request.Speaker.Name);
        if (string.IsNullOrWhiteSpace(speakerName))
        {
            throw new LtxNativeDialogueFinalPromptValidationException(["StoryCharacter.Name is required for the authoritative speaker."]);
        }

        if (request.Dialogue.Any(line => line.StoryCharacterId != request.Speaker.Id))
        {
            throw new LtxNativeDialogueFinalPromptValidationException(["A deterministic native-dialogue prompt supports exactly one resolved speaker."]);
        }

        var visualDirection = NormalizeDirection(request.VisualDirection);
        var creative = request.CreativeDirection;
        var performanceDirection = JoinNonEmpty(
            creative.PerformanceDirection,
            creative.FacialExpression,
            creative.BodyMovement,
            creative.VoiceDeliveryDirection,
            creative.TimingDirection);
        var cameraDirection = JoinNonEmpty(creative.CameraDirection, creative.EnvironmentalMotion);
        var voiceDirection = BuildVoiceDirection(request);
        var namedLines = request.Dialogue
            .OrderBy(line => line.SortOrder)
            .Select(line => $"{speakerName} says in Turkish: {Quote(line.SpokenText)}")
            .ToList();
        var onlySpeakerLine = $"Only {speakerName} speaks";
        var dialogueBlock = string.Join("\n", namedLines.Append(onlySpeakerLine));
        var combined = string.Join("\n", new[]
        {
            "[Visual Direction]",
            visualDirection,
            "[Performance Direction]",
            performanceDirection,
            "[Camera Direction]",
            cameraDirection,
            "[Voice Direction]",
            voiceDirection,
            "[Authoritative Dialogue]",
            dialogueBlock,
            "[Native Dialogue Constraints]",
            $"{speakerName} speaks audibly in natural Turkish with clear Turkish pronunciation and synchronized lip movement.",
            "No narrator. No additional dialogue. No background music.",
            "No subtitles. No captions. No on-screen text.",
            "single continuous shot, no cuts."
        });

        Validate(new LtxNativeDialogueFinalPromptValidationRequest
        {
            Prompt = combined,
            SpeakerDisplayName = request.Speaker.Name,
            ExactDialogueLines = request.Dialogue.OrderBy(line => line.SortOrder).Select(line => line.SpokenText).ToList(),
            VoiceDirection = voiceDirection,
            VisualDirection = visualDirection,
            OtherCharacterDisplayNames = request.OtherCharacterDisplayNames
        });

        return new LtxNativeDialogueFinalPrompt
        {
            CombinedPrompt = combined,
            DialogueBlock = dialogueBlock,
            VoiceDirection = voiceDirection,
            VisualDirection = visualDirection,
            SpeakerDisplayName = request.Speaker.Name,
            NamedSpeakerLines = namedLines,
            OnlySpeakerLine = onlySpeakerLine
        };
    }

    public void Validate(LtxNativeDialogueFinalPromptValidationRequest request)
    {
        var errors = new List<string>();
        var speakerName = EscapeInline(request.SpeakerDisplayName);
        if (string.IsNullOrWhiteSpace(request.Prompt)) errors.Add("Final native-dialogue prompt is empty.");
        if (string.IsNullOrWhiteSpace(speakerName)) errors.Add("StoryCharacter.Name is required for final preflight.");
        if (request.ExactDialogueLines.Count == 0) errors.Add("Authoritative exact dialogue is missing.");
        if (string.IsNullOrWhiteSpace(request.VisualDirection) || !request.Prompt.Contains(request.VisualDirection, StringComparison.Ordinal))
            errors.Add("Final prompt is missing the visual/video direction.");
        if (string.IsNullOrWhiteSpace(request.VoiceDirection) || !request.Prompt.Contains(request.VoiceDirection, StringComparison.Ordinal))
            errors.Add("Final prompt is missing the VoiceProfile direction.");
        if (request.Prompt.Length > MaxPromptCharacters)
            errors.Add($"Final prompt exceeds the safe {MaxPromptCharacters.ToString(CultureInfo.InvariantCulture)} character limit.");

        foreach (var group in request.ExactDialogueLines.GroupBy(line => line, StringComparer.Ordinal))
        {
            var canonical = $"{speakerName} says in Turkish: {Quote(group.Key)}";
            var expectedCount = group.Count();
            var actualCount = CountOccurrences(request.Prompt, canonical);
            if (actualCount != expectedCount)
                errors.Add($"Authoritative named-speaker line occurrence mismatch. Expected={expectedCount}; Actual={actualCount}.");
            var encodedDialogue = Quote(group.Key)[1..^1];
            var dialogueOccurrenceCount = CountOccurrences(request.Prompt, encodedDialogue);
            if (dialogueOccurrenceCount != expectedCount)
                errors.Add($"Authoritative dialogue text must occur only in its canonical speech line. Expected={expectedCount}; Actual={dialogueOccurrenceCount}.");
        }

        var onlySpeaker = $"Only {speakerName} speaks";
        if (CountOccurrences(request.Prompt, onlySpeaker) != 1)
            errors.Add("Final prompt must contain exactly one canonical only-speaker line.");

        foreach (var otherName in request.OtherCharacterDisplayNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var escaped = EscapeInline(otherName);
            if (ContainsSpeechAttribution(request.Prompt, escaped))
                errors.Add($"Final prompt assigns speech to another character: {escaped}.");
        }

        if (errors.Count > 0) throw new LtxNativeDialogueFinalPromptValidationException(errors);
    }

    internal static string Quote(string value) => JsonSerializer.Serialize(value ?? string.Empty, QuotedTextOptions);

    internal static string EscapeInline(string value) => (value ?? string.Empty).Trim()
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string BuildVoiceDirection(LtxNativeDialogueFinalPromptRequest request)
    {
        var profile = request.VoiceProfile;
        return string.Join("; ", new[]
        {
            $"Project language: {EscapeInline(request.ProjectLanguage)}",
            $"Voice: {EscapeInline(profile.VoiceDescription)}",
            $"Speaking style: {EscapeInline(profile.SpeakingStyle)}",
            $"Perceived age: {EscapeInline(profile.PerceivedAge)}",
            $"Gender presentation: {EscapeInline(profile.GenderPresentation)}",
            $"Accent: {EscapeInline(profile.AccentDescription)}",
            $"Pitch: {EscapeInline(profile.PitchDescription)}",
            $"Tempo: {EscapeInline(profile.TempoDescription)}"
        });
    }

    private static string NormalizeDirection(string value) => (value ?? string.Empty).Trim();

    private static string JoinNonEmpty(params string[] values) => string.Join("; ", values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(NormalizeDirection));

    private static int CountOccurrences(string value, string expected)
    {
        if (string.IsNullOrEmpty(expected)) return 0;
        var count = 0;
        for (var index = 0; (index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0; index += expected.Length) count++;
        return count;
    }

    private static bool ContainsSpeechAttribution(string prompt, string name)
    {
        var prefixes = new[] { $"{name} says", $"{name} speaks", $"{name} whispers", $"{name} shouts", $"{name} konuş", $"{name} söyl", $"{name} der" };
        return prefixes.Any(prefix => prompt.Contains(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
