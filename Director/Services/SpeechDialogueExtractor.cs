using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Models;

namespace Director.Services;

public sealed class SpeechDialogueLine
{
    public string SpeakerKey { get; set; } = string.Empty;
    public string SpeakerName { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string SpokenText { get; set; } = string.Empty;
    public string Emotion { get; set; } = "neutral";
    public int SortOrder { get; set; }
    public int StoryCharacterId { get; set; }
}

public static class SpeechDialogueExtractor
{
    public static List<SpeechDialogueLine> Extract(string? dialogueJson, IReadOnlyList<StoryCharacter> characters)
    {
        if (string.IsNullOrWhiteSpace(dialogueJson) || dialogueJson.Trim() == "[]")
        {
            return [];
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(dialogueJson);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("DialogueJson gecerli JSON degil.", ex);
        }

        var array = root as JsonArray;
        if (array is null && root is JsonObject obj)
        {
            array = obj["dialogue"] as JsonArray ?? obj["lines"] as JsonArray ?? obj["segments"] as JsonArray;
        }

        if (array is null || array.Count == 0)
        {
            return [];
        }

        var result = new List<SpeechDialogueLine>();
        var order = 1;
        foreach (var item in array.OfType<JsonObject>())
        {
            var speakerKey = ReadString(item, "characterKey", "character_key", "speakerKey", "speaker_key", "speaker");
            var speakerName = ReadString(item, "characterName", "character_name", "speakerName", "speaker_name", "name");
            var sourceText = ReadString(item, "text", "line", "dialogue", "sourceText", "source_text");
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                continue;
            }

            var character = MatchCharacter(characters, speakerKey, speakerName);
            if (character is null)
            {
                throw new InvalidOperationException($"DialogueJson konusmacisi StoryCharacter ile eslesmedi. SpeakerKey={speakerKey}; SpeakerName={speakerName}");
            }

            var spokenText = NormalizeSpokenText(sourceText);
            result.Add(new SpeechDialogueLine
            {
                SpeakerKey = character.CharacterKey,
                SpeakerName = character.Name,
                SourceText = sourceText.Trim(),
                SpokenText = spokenText,
                Emotion = ReadString(item, "emotion", "style") is { Length: > 0 } emotion ? emotion : "neutral",
                SortOrder = order++,
                StoryCharacterId = character.Id
            });
        }

        return result;
    }

    public static void ValidateNoNewSegments(IReadOnlyList<SpeechDialogueLine> sourceLines, IReadOnlyList<SpeechDialogueLine> plannedLines)
    {
        var source = sourceLines.Select(line => HashKey(line.SpeakerKey, line.SourceText)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var planned in plannedLines)
        {
            if (!source.Contains(HashKey(planned.SpeakerKey, planned.SourceText)))
            {
                throw new InvalidOperationException("Konusma plani DialogueJson disinda yeni replik iceriyor.");
            }
        }
    }

    private static StoryCharacter? MatchCharacter(IReadOnlyList<StoryCharacter> characters, string speakerKey, string speakerName)
    {
        return characters.FirstOrDefault(character => string.Equals(character.CharacterKey, speakerKey, StringComparison.OrdinalIgnoreCase)) ??
            characters.FirstOrDefault(character => !string.IsNullOrWhiteSpace(speakerName) && string.Equals(character.Name, speakerName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSpokenText(string text)
    {
        var normalized = string.Join(' ', text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length > 0 && !".!?".Contains(normalized[^1]))
        {
            normalized += ".";
        }

        return normalized;
    }

    private static string ReadString(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is not null)
            {
                return node.ToString().Trim();
            }
        }

        return string.Empty;
    }

    private static string HashKey(string speakerKey, string text) => $"{speakerKey.Trim().ToLowerInvariant()}::{text.Trim()}";
}
