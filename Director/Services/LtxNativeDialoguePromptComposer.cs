using System.IO;
using System.Security.Cryptography;
using System.Text;
using Director.Data;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class LtxNativeDialoguePromptComposer : ILtxNativeDialoguePromptComposer
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOllamaClient _ollamaClient;
    private readonly OllamaOptions _options;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;

    public LtxNativeDialoguePromptComposer(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        IGpuGenerationCoordinator gpuCoordinator,
        IOptions<OllamaOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _ollamaClient = ollamaClient;
        _gpuCoordinator = gpuCoordinator;
        _options = options.Value;
    }

    public async Task<LtxNativeDialoguePromptResult> BuildAsync(int sceneId, int referenceImageAssetId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.FilmScenes
            .Include(item => item.FilmProject)
            .Include(item => item.FilmStory)
            .ThenInclude(story => story.Characters)
            .FirstAsync(item => item.Id == sceneId, cancellationToken);
        var reference = await db.SceneMediaAssets.AsNoTracking().FirstAsync(item => item.Id == referenceImageAssetId, cancellationToken);
        if (reference.SceneId != scene.Id)
        {
            throw new InvalidOperationException("Native dialogue referans gorseli secili sahneye ait degil.");
        }

        var characters = scene.FilmStory.Characters.OrderBy(item => item.SortOrder).ToList();
        var dialogue = SpeechDialogueExtractor.Extract(scene.DialogueJson, characters);
        var result = new LtxNativeDialoguePromptResult
        {
            DialogueSourceHash = HashText(scene.DialogueJson),
            HasDialogue = dialogue.Count > 0,
            DialogueCount = dialogue.Count,
            SpeakerCount = dialogue.Select(item => item.StoryCharacterId).Distinct().Count(),
            EstimatedSpeechDurationSeconds = EstimateSpeechSeconds(dialogue.Select(item => item.SpokenText)),
            ExactSpokenLines = dialogue.Select(item => item.SpokenText).ToList(),
            SpeakerKey = dialogue.FirstOrDefault()?.SpeakerKey ?? string.Empty,
            ExactDialogue = dialogue.Count == 1 ? dialogue[0].SpokenText : string.Join(" ", dialogue.Select(item => item.SpokenText)),
            IsValid = true
        };

        if (dialogue.Count > 2)
        {
            result.Warnings.Add("10 saniyelik LTX native dialogue sahnesinde en fazla iki kisa cumle onerilir.");
            result.IsValid = false;
        }

        if (result.SpeakerCount > 1)
        {
            result.Warnings.Add("Ilk native dialogue modunda sahne basina tek ana konusmaci onerilir.");
            result.IsValid = false;
        }

        if (result.EstimatedSpeechDurationSeconds > Math.Max(1, scene.DurationSeconds - 1))
        {
            result.Warnings.Add("DialogueJson repligi hedef sahne suresine sigmayabilir.");
            result.IsValid = false;
        }

        if (dialogue.Count == 0)
        {
            result.VideoPrompt = BuildVideoPrompt(scene);
            result.AudioDialoguePrompt = string.Empty;
            result.CombinedPrompt = result.VideoPrompt;
        }
        else
        {
            foreach (var line in dialogue)
            {
                var character = characters.First(item => item.Id == line.StoryCharacterId);
                var profile = await EnsureVoiceProfileAsync(db, scene.FilmProjectId, character, cancellationToken);
                result.CharacterVoiceProfileIds.Add(profile.Id);
                result.VoiceSettingsHashes.Add(profile.SettingsHash);
            }

            var qwen = await ComposeWithQwenAsync(scene, characters, dialogue, reference.FilePath, cancellationToken);
            result.VideoPrompt = qwen.VideoPrompt.Trim();
            result.AudioDialoguePrompt = qwen.DialoguePrompt.Trim();
            result.CombinedPrompt = qwen.CombinedPrompt.Trim();
            result.EstimatedSpeechDurationSeconds = qwen.EstimatedSpeechDurationSeconds > 0
                ? qwen.EstimatedSpeechDurationSeconds
                : result.EstimatedSpeechDurationSeconds;
            result.Warnings.AddRange(qwen.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)));
            ValidateQwenDialogueResult(result, qwen, dialogue, characters);
        }
        return result;
    }

    private async Task<VideoPromptCompositionResult> ComposeWithQwenAsync(
        FilmScene scene,
        IReadOnlyList<StoryCharacter> characters,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        string referenceImagePath,
        CancellationToken cancellationToken)
    {
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(referenceImagePath, cancellationToken));
        var dialogueSummary = string.Join("\n", dialogue.Select(item =>
            $"{item.SortOrder}. speakerKey={item.SpeakerKey}; speakerName={item.SpeakerName}; emotion={item.Emotion}; exact=\"{item.SpokenText}\""));
        var characterSummary = string.Join("\n", characters.Select(item =>
            $"{item.CharacterKey} / {item.Name}: {Limit(item.PhysicalDescription + " " + item.ClothingDescription + " " + item.VoiceDescription, 520)}"));

        var messages = new List<OllamaChatMessage>
        {
            new("system", BuildNativeSystemPrompt()),
            new("user", BuildNativeUserPrompt(scene, characterSummary, dialogueSummary), [imageBase64])
        };

        await using var gpuLease = await _gpuCoordinator.AcquireAsync(
            GenerationOperationType.OllamaText,
            scene.FilmProjectId,
            scene.Id,
            cancellationToken);
        return await _ollamaClient.ChatStructuredAsync<VideoPromptCompositionResult>(
            messages,
            BuildNativeJsonSchema(),
            _options.DialogueModel,
            cancellationToken: cancellationToken);
    }

    private static void ValidateQwenDialogueResult(
        LtxNativeDialoguePromptResult result,
        VideoPromptCompositionResult qwen,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        IReadOnlyList<StoryCharacter> characters)
    {
        if (!qwen.HasDialogue || string.IsNullOrWhiteSpace(result.CombinedPrompt))
        {
            throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
        }

        if (qwen.EstimatedSpeechDurationSeconds > 8)
        {
            throw new InvalidOperationException("Turkce replik 10 saniyelik LTX klip butcesi icin fazla uzun.");
        }

        foreach (var line in dialogue)
        {
            var character = characters.First(item => item.Id == line.StoryCharacterId);
            var required = $"{character.Name} says in Turkish: \"{line.SpokenText}\"";
            if (!result.CombinedPrompt.Contains(required, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
            }
        }

        var exactSet = dialogue.Select(item => item.SpokenText).ToHashSet(StringComparer.Ordinal);
        var returnedLines = qwen.ExactDialogue
            .Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => line.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        if (returnedLines.Count == 0 || returnedLines.Any(line => !exactSet.Contains(line)))
        {
            throw new InvalidOperationException("Konusmali video promptu DialogueJson disinda replik iceriyor.");
        }

        foreach (var required in new[]
        {
            "speaks audibly in natural Turkish",
            "clear Turkish pronunciation",
            "synchronized lip movement",
            "No narrator",
            "No additional dialogue",
            "No background music",
            "No subtitles",
            "No captions",
            "No on-screen text",
            "single continuous shot",
            "no cuts"
        })
        {
            if (!result.CombinedPrompt.Contains(required, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
            }
        }
    }

    private static string BuildVideoPrompt(FilmScene scene)
    {
        return string.Join("\n", new[]
        {
            "Single continuous cinematic shot based on the supplied start image.",
            "The character preserves the same face, clothing, age, body proportions and environment.",
            "Natural motion, stable camera, synchronized lip movement when speech is present.",
            string.IsNullOrWhiteSpace(scene.VideoPrompt) ? scene.SceneDescription : scene.VideoPrompt,
            "The shot remains continuous with no cuts."
        });
    }

    private static string BuildNativeSystemPrompt()
    {
        return """
You are a cinematic Qwen image-to-video prompt composer for LTX native audio-video generation.
Return only valid structured JSON matching the schema.
Use the reference image, story context, character data, existing video prompt and DialogueJson-derived Turkish dialogue.
Never invent new dialogue. Never translate the Turkish line. Never change the speaker. Never use narration text. Never request subtitles, captions, on-screen text, music, extra dialogue, cuts, edits, or multiple camera angles.
For dialogue scenes, combinedPrompt must contain the exact Turkish line in double quotes and must include: speaks audibly in natural Turkish, clear Turkish pronunciation, synchronized lip movement, [Character] says in Turkish: "...", Only [Character] speaks, No narrator, No additional dialogue, No background music, No subtitles, No captions, No on-screen text, single continuous shot, no cuts.
Place the speech naturally inside a 10 second clip with short silent motion before and after. If estimated speech is longer than 8 seconds, report that in estimatedSpeechDurationSeconds and warnings.
""";
    }

    private static string BuildNativeUserPrompt(FilmScene scene, string characters, string dialogueSummary)
    {
        return $"""
Prepare a 10 second LTX native Turkish talking-video prompt.
Scene number: {scene.SceneNumber}
Scene title: {scene.Title}
Story beat: {Limit(scene.StoryBeat, 900)}
Scene description: {Limit(scene.SceneDescription, 900)}
Existing VideoPrompt: {Limit(scene.VideoPrompt, 1200)}
Location/time: {scene.LocationDescription}; {scene.TimeOfDay}
Characters:
{characters}

DialogueJson-derived exact dialogue, the only speech source:
{dialogueSummary}

Return structured JSON. combinedPrompt must be the final prompt for WanGP. exactDialogue must contain only the exact DialogueJson Turkish line or lines, with multiple lines separated by " | ".
""";
    }

    private static object BuildNativeJsonSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                hasDialogue = new { type = "boolean" },
                videoPrompt = new { type = "string" },
                dialoguePrompt = new { type = "string" },
                combinedPrompt = new { type = "string" },
                speakerKey = new { type = "string" },
                exactDialogue = new { type = "string" },
                estimatedSpeechDurationSeconds = new { type = "number" },
                videoNegativePrompt = new { type = "string" },
                motionSummary = new { type = "string" },
                subjectActions = new { type = "array", items = new { type = "string" } },
                cameraMovement = new { type = "string" },
                environmentMotion = new { type = "array", items = new { type = "string" } },
                startState = new { type = "string" },
                endState = new { type = "string" },
                continuityPreserved = new { type = "array", items = new { type = "string" } },
                warnings = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "hasDialogue", "videoPrompt", "dialoguePrompt", "combinedPrompt", "speakerKey", "exactDialogue", "estimatedSpeechDurationSeconds", "warnings" }
        };
    }

    private static async Task<LtxNativeVoiceProfile> EnsureVoiceProfileAsync(AppDbContext db, int filmProjectId, StoryCharacter character, CancellationToken cancellationToken)
    {
        var profile = await db.LtxNativeVoiceProfiles.FirstOrDefaultAsync(item =>
            item.FilmProjectId == filmProjectId && item.StoryCharacterId == character.Id,
            cancellationToken);
        if (profile is not null)
        {
            return profile;
        }

        var descriptor = DefaultDescriptor(character);
        profile = new LtxNativeVoiceProfile
        {
            FilmProjectId = filmProjectId,
            StoryCharacterId = character.Id,
            VoiceDescription = descriptor.VoiceDescription,
            Language = "tr",
            SpeakingStyle = descriptor.SpeakingStyle,
            PerceivedAge = descriptor.PerceivedAge,
            GenderPresentation = descriptor.GenderPresentation,
            AccentDescription = "clear Istanbul Turkish pronunciation",
            PitchDescription = descriptor.PitchDescription,
            TempoDescription = "calm natural tempo",
            IsLocked = true,
            CreatedAt = DateTime.Now
        };
        profile.SettingsHash = LtxNativeVoiceSettingsHasher.Compute(profile);
        db.LtxNativeVoiceProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private static (string VoiceDescription, string SpeakingStyle, string PerceivedAge, string GenderPresentation, string PitchDescription) DefaultDescriptor(StoryCharacter character)
    {
        var roleText = (character.Role + " " + character.VoiceDescription + " " + character.PhysicalDescription).ToLowerInvariant();
        var gender = roleText.Contains("female") || roleText.Contains("woman") || roleText.Contains("girl") || roleText.Contains("kadin")
            ? "female"
            : roleText.Contains("male") || roleText.Contains("man") || roleText.Contains("boy") || roleText.Contains("erkek")
                ? "male"
                : "neutral";
        var pitch = gender == "female" ? "medium pitch" : gender == "male" ? "medium-low pitch" : "medium pitch";
        var age = roleText.Contains("child") || roleText.Contains("cocuk") ? "child" : roleText.Contains("old") || roleText.Contains("elder") || roleText.Contains("yasli") ? "older adult" : "young adult";
        var style = "warm, clear and reassuring delivery";
        var description = $"a warm {age} Turkish {gender} voice, {pitch}, calm tempo, clear Istanbul Turkish pronunciation, gentle and reassuring delivery";
        return (description, style, age, gender, pitch);
    }

    private static double EstimateSpeechSeconds(IEnumerable<string> lines)
    {
        var words = lines.Sum(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        return words / 2.4;
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string Limit(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
