using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Director.Data;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class LtxNativeDialoguePromptComposer : ILtxNativeDialoguePromptComposer
{
    public const string OperationName = "LtxNativeDialoguePromptComposition";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOllamaClient _ollamaClient;
    private readonly OllamaOptions _options;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IOllamaFailureDiagnosticWriter _diagnosticWriter;
    private readonly ILtxNativeDialogueFinalPromptBuilder _finalPromptBuilder;
    private readonly ILogger<LtxNativeDialoguePromptComposer> _logger;

    public LtxNativeDialoguePromptComposer(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        IGpuGenerationCoordinator gpuCoordinator,
        IOllamaFailureDiagnosticWriter diagnosticWriter,
        ILtxNativeDialogueFinalPromptBuilder finalPromptBuilder,
        IOptions<OllamaOptions> options,
        ILogger<LtxNativeDialoguePromptComposer> logger)
    {
        _dbContextFactory = dbContextFactory;
        _ollamaClient = ollamaClient;
        _gpuCoordinator = gpuCoordinator;
        _diagnosticWriter = diagnosticWriter;
        _finalPromptBuilder = finalPromptBuilder;
        _options = options.Value;
        _logger = logger;
    }

    public Task<LtxNativeDialoguePromptResult> BuildAsync(
        int sceneId,
        int referenceImageAssetId,
        CancellationToken cancellationToken = default) =>
        BuildCoreAsync(sceneId, referenceImageAssetId, persistMissingVoiceProfiles: true, allowRepair: true, cancellationToken);

    public Task<LtxNativeDialoguePromptResult> BuildReadOnlyAsync(
        int sceneId,
        int referenceImageAssetId,
        bool allowRepair = false,
        CancellationToken cancellationToken = default) =>
        BuildCoreAsync(sceneId, referenceImageAssetId, persistMissingVoiceProfiles: false, allowRepair, cancellationToken);

    private async Task<LtxNativeDialoguePromptResult> BuildCoreAsync(
        int sceneId,
        int referenceImageAssetId,
        bool persistMissingVoiceProfiles,
        bool allowRepair,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.FilmScenes
            .AsNoTracking()
            .Include(item => item.FilmProject)
            .Include(item => item.FilmStory)
            .ThenInclude(story => story.Characters)
            .FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken);
        if (scene is null)
        {
            throw CreateFailure(0, sceneId, 0, NativeDialoguePromptFailureStage.SceneInputValidation, "Sahne bulunamadı.");
        }

        var reference = await db.SceneMediaAssets.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == referenceImageAssetId, cancellationToken);
        if (reference is null || reference.SceneId != scene.Id)
        {
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber, NativeDialoguePromptFailureStage.SceneInputValidation, "Seçili referans görsel bu sahneye ait değil.");
        }

        if (string.IsNullOrWhiteSpace(reference.FilePath) || !File.Exists(reference.FilePath))
        {
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber, NativeDialoguePromptFailureStage.SceneInputValidation, "Seçili referans görsel dosyası bulunamadı.");
        }

        var characters = scene.FilmStory.Characters.OrderBy(item => item.SortOrder).ToList();
        List<SpeechDialogueLine> dialogue;
        try
        {
            dialogue = SpeechDialogueExtractor.Extract(scene.DialogueJson, characters);
        }
        catch (SpeechDialogueExtractionException ex)
        {
            var stage = ex.Failure == SpeechDialogueExtractionFailure.InvalidJson
                ? NativeDialoguePromptFailureStage.DialogueJsonParsing
                : NativeDialoguePromptFailureStage.SpeakerResolution;
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber, stage, ex.Message, ex.SpeakerKey, ex);
        }

        var result = CreateBaseResult(scene, dialogue);
        if (dialogue.Any(line => string.IsNullOrWhiteSpace(line.SpeakerName)))
        {
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber,
                NativeDialoguePromptFailureStage.SpeakerResolution,
                "Eşleşen StoryCharacter.Name boş olamaz.", dialogue.FirstOrDefault()?.SpeakerKey);
        }
        ValidateSceneDomain(scene, result);
        if (dialogue.Count == 0)
        {
            result.VideoPrompt = BuildVideoPrompt(scene);
            result.CombinedPrompt = result.VideoPrompt;
            result.VoiceProfileSource = "NotRequired";
            result.ValidationResult = "VisualOnly";
            return result;
        }

        var pendingProfiles = new List<LtxNativeVoiceProfile>();
        var resolvedProfiles = new List<LtxNativeVoiceProfile>();
        var profileSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var characterId in dialogue.Select(item => item.StoryCharacterId).Distinct())
        {
            var character = characters.Single(item => item.Id == characterId);
            var profile = await db.LtxNativeVoiceProfiles.AsNoTracking().FirstOrDefaultAsync(item =>
                item.FilmProjectId == scene.FilmProjectId && item.StoryCharacterId == character.Id,
                cancellationToken);
            if (profile is null)
            {
                profile = CreateDefaultProfile(scene.FilmProjectId, character);
                pendingProfiles.Add(profile);
                profileSources.Add("GeneratedInMemory");
            }
            else
            {
                profileSources.Add("Database");
            }

            var missingFields = ValidateVoiceProfile(profile);
            if (missingFields.Count > 0)
            {
                throw CreateFailure(
                    scene.FilmProjectId,
                    scene.Id,
                    scene.SceneNumber,
                    profile.Id == 0 ? NativeDialoguePromptFailureStage.VoiceProfileGeneration : NativeDialoguePromptFailureStage.VoiceProfileLookup,
                    $"{character.CharacterKey} ses profilinde zorunlu alanlar eksik: {string.Join(", ", missingFields)}.",
                    character.CharacterKey);
            }

            resolvedProfiles.Add(profile);
            result.VoiceSettingsHashes.Add(profile.SettingsHash);
        }

        result.VoiceProfileSource = string.Join('+', profileSources.OrderBy(item => item));
        var qwen = await ComposeWithQwenAsync(scene, characters, dialogue, reference.FilePath, result, allowRepair, cancellationToken);
        try
        {
            var finalPrompt = BuildFinalPrompt(scene, characters, dialogue, resolvedProfiles, qwen.Value, scene.FilmProject.Language);
            AssembleValidatedResult(result, qwen.Value, finalPrompt);
            result.OtherCharacterDisplayNames.AddRange(characters
                .Where(item => item.Id != dialogue[0].StoryCharacterId)
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
        }
        catch (LtxNativeDialogueFinalPromptValidationException ex)
        {
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber,
                NativeDialoguePromptFailureStage.PromptAssembly, string.Join(" ", ex.Errors),
                dialogue[0].SpeakerKey, ex, result.DiagnosticPath);
        }

        if (persistMissingVoiceProfiles && pendingProfiles.Count > 0)
        {
            try
            {
                db.LtxNativeVoiceProfiles.AddRange(pendingProfiles);
                await db.SaveChangesAsync(cancellationToken);
                result.VoiceProfileSource = "GeneratedAndPersisted";
            }
            catch (Exception ex)
            {
                throw CreateFailure(
                    scene.FilmProjectId,
                    scene.Id,
                    scene.SceneNumber,
                    NativeDialoguePromptFailureStage.VoiceProfileGeneration,
                    "Doğrulanmış yerel ses profili kaydedilemedi.",
                    dialogue.FirstOrDefault()?.SpeakerKey,
                    ex);
            }
        }

        result.CharacterVoiceProfileIds.Clear();
        result.CharacterVoiceProfileIds.AddRange(resolvedProfiles.Select(item => item.Id));
        return result;
    }

    internal async Task<OllamaStructuredResult<LtxNativeDialogueCreativeDirectionResult>> ComposeForTestingAsync(
        FilmScene scene,
        IReadOnlyList<StoryCharacter> characters,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        string referenceImagePath,
        LtxNativeDialoguePromptResult result,
        bool allowRepair,
        CancellationToken cancellationToken = default) =>
        await ComposeWithQwenAsync(scene, characters, dialogue, referenceImagePath, result, allowRepair, cancellationToken);

    internal LtxNativeDialogueFinalPrompt BuildFinalPromptForTesting(
        FilmScene scene,
        IReadOnlyList<StoryCharacter> characters,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        LtxNativeVoiceProfile profile,
        LtxNativeDialogueCreativeDirectionResult creative,
        string projectLanguage = "Türkçe") =>
        BuildFinalPrompt(scene, characters, dialogue, [profile], creative, projectLanguage);

    internal static void ValidateDialogueDomainForTesting(FilmScene scene, IReadOnlyList<SpeechDialogueLine> dialogue)
    {
        if (dialogue.Any(line => string.IsNullOrWhiteSpace(line.SpeakerName)))
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber,
                NativeDialoguePromptFailureStage.SpeakerResolution, "Eşleşen StoryCharacter.Name boş olamaz.");
        ValidateSceneDomain(scene, CreateBaseResult(scene, dialogue));
    }

    private LtxNativeDialogueFinalPrompt BuildFinalPrompt(
        FilmScene scene,
        IReadOnlyList<StoryCharacter> characters,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        IReadOnlyList<LtxNativeVoiceProfile> profiles,
        LtxNativeDialogueCreativeDirectionResult creative,
        string projectLanguage)
    {
        var speaker = characters.Single(item => item.Id == dialogue[0].StoryCharacterId);
        return _finalPromptBuilder.Build(new LtxNativeDialogueFinalPromptRequest
        {
            VisualDirection = BuildVideoPrompt(scene),
            CreativeDirection = creative,
            Speaker = speaker,
            VoiceProfile = profiles.Single(item => item.StoryCharacterId == speaker.Id),
            Dialogue = dialogue,
            ProjectLanguage = projectLanguage,
            OtherCharacterDisplayNames = characters.Where(item => item.Id != speaker.Id).Select(item => item.Name).ToList()
        });
    }

    private async Task<OllamaStructuredResult<LtxNativeDialogueCreativeDirectionResult>> ComposeWithQwenAsync(
        FilmScene scene,
        IReadOnlyList<StoryCharacter> characters,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        string referenceImagePath,
        LtxNativeDialoguePromptResult result,
        bool allowRepair,
        CancellationToken cancellationToken)
    {
        var imageBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(referenceImagePath, cancellationToken));
        var dialogueSummary = string.Join("\n", dialogue.Select(item =>
            $"{item.SortOrder}. authoritativeSpeakerKey={item.SpeakerKey}; speakerName={item.SpeakerName}; emotion={item.Emotion}; dialogueText=withheld-for-application-assembly"));
        var characterSummary = string.Join("\n", characters.Select(item =>
            $"{item.CharacterKey} / {item.Name}: {Limit(item.PhysicalDescription + " " + item.ClothingDescription + " " + item.VoiceDescription, 520)}"));
        var messages = new List<OllamaChatMessage>
        {
            new("system", BuildNativeSystemPrompt()),
            new("user", BuildNativeUserPrompt(scene, characterSummary, dialogueSummary), [imageBase64])
        };
        var schema = BuildNativeJsonSchema();
        var correlationId = Guid.NewGuid().ToString("N");
        var context = new OllamaFailureContext(
            scene.FilmProjectId,
            scene.SceneNumber,
            OperationName,
            scene.Id,
            dialogue.FirstOrDefault()?.StoryCharacterId,
            dialogue.FirstOrDefault()?.SpeakerKey,
            correlationId);

        OllamaResponseException? initialFailure = null;
        try
        {
            var initial = await CallModelAsync(scene, messages, schema, cancellationToken);
            ValidateDetailedResult(initial, dialogue, characters);
            CopyDiagnostics(result, initial, repairUsed: false, correlationId);
            return initial;
        }
        catch (OllamaResponseException ex) when (IsRepairable(ex))
        {
            initialFailure = ex;
            result.DiagnosticPath = await WriteDiagnosticBestEffortAsync(context, "initial", ex, cancellationToken);
            if (!allowRepair)
            {
                throw ToTypedFailure(scene, dialogue, ex, result.DiagnosticPath);
            }
        }
        catch (OllamaResponseException ex)
        {
            result.DiagnosticPath = await WriteDiagnosticBestEffortAsync(context, "initial", ex, cancellationToken);
            throw ToTypedFailure(scene, dialogue, ex, result.DiagnosticPath);
        }

        var repairMessages = new List<OllamaChatMessage>
        {
            new("system", "Repair one malformed LTX native-dialogue creative-direction response. Return exactly one JSON object matching the supplied schema. Do not output dialogue text, speaker fields, final prompts, markdown or explanation."),
            new("user", $"Expected schema:\n{JsonSerializer.Serialize(schema, JsonOptions)}\n\nMalformed response:\n{initialFailure!.ResponseContent}")
        };
        try
        {
            var repaired = await CallModelAsync(scene, repairMessages, schema, cancellationToken, deterministic: true);
            ValidateDetailedResult(repaired, dialogue, characters);
            CopyDiagnostics(result, repaired, repairUsed: true, correlationId);
            return repaired;
        }
        catch (OllamaResponseException ex)
        {
            result.DiagnosticPath = await WriteDiagnosticBestEffortAsync(context, "repair", ex, cancellationToken);
            throw ToTypedFailure(scene, dialogue, ex, result.DiagnosticPath);
        }
    }

    private async Task<OllamaStructuredResult<LtxNativeDialogueCreativeDirectionResult>> CallModelAsync(
        FilmScene scene,
        IReadOnlyList<OllamaChatMessage> messages,
        object schema,
        CancellationToken cancellationToken,
        bool deterministic = false)
    {
        var settings = new OllamaGenerationSettings
        {
            OperationName = OperationName,
            FilmProjectId = scene.FilmProjectId,
            SceneNumber = scene.SceneNumber,
            Think = false,
            Temperature = deterministic ? 0 : null,
            TopP = deterministic ? 0.1 : null
        };
        await using var gpuLease = await _gpuCoordinator.AcquireAsync(
            GenerationOperationType.OllamaText,
            scene.FilmProjectId,
            scene.Id,
            cancellationToken);
        return await _ollamaClient.ChatStructuredDetailedAsync<LtxNativeDialogueCreativeDirectionResult>(
            messages,
            schema,
            _options.DialogueModel,
            cancellationToken: cancellationToken,
            generationSettings: settings);
    }

    private static void ValidateDetailedResult(
        OllamaStructuredResult<LtxNativeDialogueCreativeDirectionResult> detailed,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        IReadOnlyList<StoryCharacter> characters)
    {
        var errors = ValidateCreativeDirectionResult(detailed.Value, dialogue, characters);
        if (errors.Count == 0)
        {
            return;
        }

        throw new OllamaStructuredResponseException(
            "Native-dialogue cevabı semantic validation kurallarından geçemedi.",
            "ResponseValidation",
            detailed.RawResponse,
            detailed.Metadata)
        {
            ValidationErrors = errors
        };
    }

    internal static IReadOnlyList<string> ValidateCreativeDirectionResult(
        LtxNativeDialogueCreativeDirectionResult creative,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        IReadOnlyList<StoryCharacter> characters)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(creative.PerformanceDirection)) errors.Add("performanceDirection is required.");
        if (string.IsNullOrWhiteSpace(creative.FacialExpression)) errors.Add("facialExpression is required.");
        if (string.IsNullOrWhiteSpace(creative.BodyMovement)) errors.Add("bodyMovement is required.");
        if (string.IsNullOrWhiteSpace(creative.VoiceDeliveryDirection)) errors.Add("voiceDeliveryDirection is required.");
        if (string.IsNullOrWhiteSpace(creative.CameraDirection)) errors.Add("cameraDirection is required.");
        if (string.IsNullOrWhiteSpace(creative.EnvironmentalMotion)) errors.Add("environmentalMotion is required.");
        if (string.IsNullOrWhiteSpace(creative.TimingDirection)) errors.Add("timingDirection is required.");

        var creativeText = string.Join("\n", new[]
        {
            creative.PerformanceDirection, creative.FacialExpression, creative.BodyMovement,
            creative.VoiceDeliveryDirection, creative.CameraDirection, creative.EnvironmentalMotion,
            creative.TimingDirection
        });
        if (dialogue.Any(line => creativeText.Contains(line.SpokenText, StringComparison.Ordinal)))
            errors.Add("Creative directions must not repeat authoritative DialogueJson text.");
        if (creativeText.Contains('"'))
            errors.Add("Creative directions contain quoted content that could introduce new dialogue.");
        if (new[] { "says in Turkish", "says:", "speaks:", "dialogue:", "line:", "diyor:", "söyler:", "şöyle der:" }
            .Any(marker => creativeText.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            errors.Add("Creative directions contain speech-like content that could introduce new dialogue.");

        var extensionKeys = creative.AdditionalFields?.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var forbidden in extensionKeys.Where(key =>
                     !string.Equals(key, "combinedPrompt", StringComparison.OrdinalIgnoreCase) &&
                     (key.Contains("dialogue", StringComparison.OrdinalIgnoreCase) ||
                      key.Contains("speaker", StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add($"Model response contains forbidden dialogue field: {forbidden}.");
        }

        var speakerId = dialogue.FirstOrDefault()?.StoryCharacterId;
        foreach (var other in characters.Where(item => item.Id != speakerId && !string.IsNullOrWhiteSpace(item.Name)))
        {
            if (ContainsSpeechAttribution(creativeText, other.Name))
                errors.Add($"Creative directions assign speech to another character: {other.CharacterKey}.");
        }

        return errors;
    }

    private static bool ContainsSpeechAttribution(string text, string name) => new[]
    {
        $"{name} says", $"{name} speaks", $"{name} whispers", $"{name} shouts",
        $"{name} konuş", $"{name} söyl", $"{name} der"
    }.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    internal static IReadOnlyList<string> ValidateVoiceProfile(LtxNativeVoiceProfile profile)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.VoiceDescription)) missing.Add(nameof(profile.VoiceDescription));
        if (string.IsNullOrWhiteSpace(profile.Language)) missing.Add(nameof(profile.Language));
        if (string.IsNullOrWhiteSpace(profile.SpeakingStyle)) missing.Add(nameof(profile.SpeakingStyle));
        if (string.IsNullOrWhiteSpace(profile.PerceivedAge)) missing.Add(nameof(profile.PerceivedAge));
        if (string.IsNullOrWhiteSpace(profile.GenderPresentation)) missing.Add(nameof(profile.GenderPresentation));
        if (string.IsNullOrWhiteSpace(profile.AccentDescription)) missing.Add(nameof(profile.AccentDescription));
        if (string.IsNullOrWhiteSpace(profile.PitchDescription)) missing.Add(nameof(profile.PitchDescription));
        if (string.IsNullOrWhiteSpace(profile.TempoDescription)) missing.Add(nameof(profile.TempoDescription));
        if (string.IsNullOrWhiteSpace(profile.SettingsHash)) missing.Add(nameof(profile.SettingsHash));
        return missing;
    }

    private async Task<string> WriteDiagnosticBestEffortAsync(
        OllamaFailureContext context,
        string attemptType,
        OllamaResponseException exception,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _diagnosticWriter.WriteAsync(context, attemptType, exception, cancellationToken);
        }
        catch (Exception diagnosticException)
        {
            _logger.LogError(diagnosticException, "Native dialogue diagnostic could not be written. ProjectId={ProjectId}; SceneId={SceneId}; SceneNumber={SceneNumber}; CorrelationId={CorrelationId}", context.FilmProjectId, context.SceneId, context.SceneNumber, context.CorrelationId);
            return string.Empty;
        }
    }

    private static bool IsRepairable(OllamaResponseException exception) =>
        exception is OllamaStructuredResponseException;

    private static NativeDialoguePromptCompositionException ToTypedFailure(
        FilmScene scene,
        IReadOnlyList<SpeechDialogueLine> dialogue,
        OllamaResponseException exception,
        string diagnosticPath)
    {
        var stage = exception is OllamaStructuredResponseException
            ? exception.Stage == "ResponseValidation"
                ? NativeDialoguePromptFailureStage.ResponseValidation
                : NativeDialoguePromptFailureStage.OllamaResponseParsing
            : NativeDialoguePromptFailureStage.OllamaTransport;
        var reason = exception.ValidationErrors.Count > 0
            ? string.Join(" ", exception.ValidationErrors.Take(3))
            : exception.Message;
        return CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber, stage, reason, dialogue.FirstOrDefault()?.SpeakerKey, exception, diagnosticPath);
    }

    private static void CopyDiagnostics(
        LtxNativeDialoguePromptResult target,
        OllamaStructuredResult<LtxNativeDialogueCreativeDirectionResult> source,
        bool repairUsed,
        string correlationId)
    {
        target.Model = source.Metadata.Model;
        target.PromptTokenCount = source.Metadata.PromptTokenCount;
        target.ResponseTokenCount = source.Metadata.ResponseTokenCount;
        target.ResponseCharacterCount = source.Metadata.ResponseCharacterCount;
        target.Done = source.Metadata.Done;
        target.DoneReason = source.Metadata.DoneReason;
        target.RawResponseShape = ClassifyRawResponseShape(source.RawResponse);
        target.ParseStage = "CentralStructuredJson";
        target.ValidationResult = "Passed";
        target.RepairUsed = repairUsed;
        target.DiagnosticCorrelationId = correlationId;
    }

    internal static string ClassifyRawResponseShape(string rawResponse)
    {
        var trimmed = rawResponse.TrimStart('\uFEFF').Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) return "CodeFence";
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}')) return "JsonObject";
        if (trimmed.Contains('{') && trimmed.Contains('}')) return "ExplanationWithJson";
        return string.IsNullOrWhiteSpace(trimmed) ? "Empty" : "PlainText";
    }

    private static void AssembleValidatedResult(
        LtxNativeDialoguePromptResult result,
        LtxNativeDialogueCreativeDirectionResult creative,
        LtxNativeDialogueFinalPrompt finalPrompt)
    {
        result.VideoPrompt = finalPrompt.VisualDirection;
        result.AudioDialoguePrompt = finalPrompt.DialogueBlock;
        result.CombinedPrompt = finalPrompt.CombinedPrompt;
        result.SpeakerDisplayName = finalPrompt.SpeakerDisplayName;
        result.VoiceDirection = finalPrompt.VoiceDirection;
        result.NamedSpeakerCanonicalLines.AddRange(finalPrompt.NamedSpeakerLines);
        result.OnlySpeakerCanonicalLine = finalPrompt.OnlySpeakerLine;
        result.Warnings.AddRange(creative.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)));
        result.ModelReturnedCombinedPrompt = creative.AdditionalFields?.ContainsKey("combinedPrompt") == true;
        result.IsValid = true;
    }

    private static LtxNativeDialoguePromptResult CreateBaseResult(FilmScene scene, IReadOnlyList<SpeechDialogueLine> dialogue) =>
        new()
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            SceneNumber = scene.SceneNumber,
            DialogueSourceHash = HashText(scene.DialogueJson),
            HasDialogue = dialogue.Count > 0,
            DialogueCount = dialogue.Count,
            SpeakerCount = dialogue.Select(item => item.StoryCharacterId).Distinct().Count(),
            EstimatedSpeechDurationSeconds = EstimateSpeechSeconds(dialogue.Select(item => item.SpokenText)),
            ExactSpokenLines = dialogue.Select(item => item.SpokenText).ToList(),
            SpeakerKey = dialogue.FirstOrDefault()?.SpeakerKey ?? string.Empty,
            ExactDialogue = dialogue.Count == 1 ? dialogue[0].SpokenText : string.Join(" | ", dialogue.Select(item => item.SpokenText)),
            IsValid = true
        };

    private static void ValidateSceneDomain(FilmScene scene, LtxNativeDialoguePromptResult result)
    {
        var errors = new List<string>();
        if (result.DialogueCount > 2) errors.Add("10 saniyelik sahnede en fazla iki kısa diyalog satırı desteklenir.");
        if (result.SpeakerCount > 1) errors.Add("Bu native-dialogue akışında sahne başına tek konuşmacı desteklenir.");
        if (result.EstimatedSpeechDurationSeconds > Math.Max(1, scene.DurationSeconds - 1)) errors.Add("Diyalog hedef sahne süresine sığmıyor.");
        if (errors.Count > 0)
        {
            throw CreateFailure(scene.FilmProjectId, scene.Id, scene.SceneNumber, NativeDialoguePromptFailureStage.SceneInputValidation, string.Join(' ', errors), result.SpeakerKey);
        }
    }

    internal static LtxNativeVoiceProfile CreateDefaultProfile(int filmProjectId, StoryCharacter character)
    {
        var descriptor = DefaultDescriptor(character);
        var profile = new LtxNativeVoiceProfile
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
        return profile;
    }

    private static NativeDialoguePromptCompositionException CreateFailure(
        int filmProjectId,
        int sceneId,
        int sceneNumber,
        NativeDialoguePromptFailureStage stage,
        string reason,
        string? characterKey = null,
        Exception? innerException = null,
        string diagnosticPath = "") =>
        new(filmProjectId, sceneId, sceneNumber, stage, reason, diagnosticPath, characterKey, innerException);

    private static string BuildVideoPrompt(FilmScene scene) => string.Join("\n", new[]
    {
        "Single continuous cinematic shot based on the supplied start image.",
        "The character preserves the same face, clothing, age, body proportions and environment.",
        "Natural motion and stable camera.",
        string.IsNullOrWhiteSpace(scene.VideoPrompt) ? scene.SceneDescription : scene.VideoPrompt,
        "The shot remains continuous with no cuts."
    });

    private static string BuildNativeSystemPrompt() =>
        """
You are a cinematic Qwen image-to-video prompt composer for LTX native audio-video generation.
Return only valid structured JSON matching the schema.
Use the reference image, story context, character data and existing video prompt only to produce creative direction fields.
The application will append the authoritative speaker name, exact Turkish dialogue, voice profile and all native-dialogue boilerplate after your response.
Do not repeat or invent dialogue text. Do not return speakerKey, speakerName, exactDialogue, dialoguePrompt, combinedPrompt or any final prompt.
Do not assign speech to another character. Produce only performance, facial expression, body movement, voice delivery, camera, environmental motion and timing direction.
""";

    private static string BuildNativeUserPrompt(FilmScene scene, string characters, string dialogueSummary) =>
        $"""
Prepare a 10 second LTX native Turkish talking-video prompt.
Scene number: {scene.SceneNumber}
Scene title: {scene.Title}
Story beat: {Limit(scene.StoryBeat, 900)}
Scene description: {Limit(scene.SceneDescription, 900)}
Existing VideoPrompt: {Limit(scene.VideoPrompt, 1200)}
Location/time: {scene.LocationDescription}; {scene.TimeOfDay}
Characters:
{characters}

Authoritative dialogue metadata (text intentionally withheld; application-owned):
{dialogueSummary}

Return only the creative-direction structured JSON. Do not include dialogue text or final prompt boilerplate.
""";

    private static object BuildNativeJsonSchema() => new
    {
        type = "object",
        properties = new
        {
            performanceDirection = new { type = "string" },
            facialExpression = new { type = "string" },
            bodyMovement = new { type = "string" },
            voiceDeliveryDirection = new { type = "string" },
            cameraDirection = new { type = "string" },
            environmentalMotion = new { type = "string" },
            timingDirection = new { type = "string" },
            warnings = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "performanceDirection", "facialExpression", "bodyMovement", "voiceDeliveryDirection", "cameraDirection", "environmentalMotion", "timingDirection", "warnings" }
    };

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

    private static double EstimateSpeechSeconds(IEnumerable<string> lines) =>
        lines.Sum(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length) / 2.4;

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Limit(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
