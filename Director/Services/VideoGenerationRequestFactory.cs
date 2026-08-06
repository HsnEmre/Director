using System.Text.Json;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Services.Interfaces;
using Director.WanGp;

namespace Director.Services;

public sealed class VideoGenerationRequestFactory : IVideoGenerationRequestFactory
{
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpVideoInputContractResolver _inputContractResolver;
    private readonly ILtxNativeDialoguePromptComposer _ltxNativeDialoguePromptComposer;
    private readonly ILtxNativeDialogueCapabilityResolver _ltxNativeDialogueCapabilityResolver;
    private readonly IVideoModelCapabilityService _videoModelCapabilityService;

    public VideoGenerationRequestFactory(
        IWanGpClient wanGpClient,
        IWanGpVideoInputContractResolver inputContractResolver,
        ILtxNativeDialoguePromptComposer ltxNativeDialoguePromptComposer,
        ILtxNativeDialogueCapabilityResolver ltxNativeDialogueCapabilityResolver,
        IVideoModelCapabilityService videoModelCapabilityService)
    {
        _wanGpClient = wanGpClient;
        _inputContractResolver = inputContractResolver;
        _ltxNativeDialoguePromptComposer = ltxNativeDialoguePromptComposer;
        _ltxNativeDialogueCapabilityResolver = ltxNativeDialogueCapabilityResolver;
        _videoModelCapabilityService = videoModelCapabilityService;
    }

    public async Task<WanGpVideoGenerationRequest> CreateAsync(
        VideoGenerationRequestFactoryInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input.Scene);
        ArgumentNullException.ThrowIfNull(input.SourceImageAsset);

        var model = await ResolveModelAsync(input.ModelType, cancellationToken);
        var schema = await _wanGpClient.GetModelSchemaAsync(model.ModelType, cancellationToken);
        var defaults = schema is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : ToObjectDictionary(schema.DefaultSettings);
        var inputContract = await _inputContractResolver.ResolveAsync(model, schema, defaults, cancellationToken);
        if (!inputContract.IsValidated || !inputContract.SupportsStartImage)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(inputContract.FailureReason)
                ? "Secili video modelinin WanGP Start Image sozlesmesi dogrulanamadi."
                : inputContract.FailureReason);
        }

        var hasDialogue = SceneHasDialogue(input.Scene);
        var generationMode = hasDialogue && input.PreferNativeDialogue
            ? VideoAudioGenerationMode.LtxNativeDialogue
            : VideoAudioGenerationMode.SilentVideo;

        var prompt = string.IsNullOrWhiteSpace(input.PromptOverride)
            ? input.Scene.VideoPrompt
            : input.PromptOverride;
        var dialogueHash = string.Empty;
        var nativeProfileIds = new List<int>();
        var nativeProfileHashes = new List<string>();
        var exactSpokenLines = new List<string>();
        var dialogueCount = 0;
        var speakerCount = 0;
        var nativeSpeakerDisplayName = string.Empty;
        var nativeVoiceDirection = string.Empty;
        var nativeVisualDirection = string.Empty;
        var otherCharacterDisplayNames = new List<string>();
        var capability = _ltxNativeDialogueCapabilityResolver.Resolve(model, inputContract, model.InstallStatus);
        var requestedDurationSeconds = generationMode == VideoAudioGenerationMode.LtxNativeDialogue
            ? VideoModelCapabilityService.VerifiedLtxDurationSeconds
            : Math.Max(1, input.Scene.DurationSeconds);
        var durationValidation = _videoModelCapabilityService.ValidateDuration(model.ModelType, requestedDurationSeconds);
        if (!durationValidation.IsValid)
        {
            throw new InvalidOperationException(durationValidation.ErrorMessage);
        }

        if (generationMode == VideoAudioGenerationMode.LtxNativeDialogue)
        {
            if (!capability.IsSupported)
            {
                throw new InvalidOperationException($"Secili model LTX native dialogue icin uygun degil: {capability.FailureReason}");
            }

            var nativePrompt = await _ltxNativeDialoguePromptComposer.BuildAsync(
                input.Scene.Id,
                input.SourceImageAsset.Id,
                cancellationToken);
            if (!nativePrompt.IsValid)
            {
                throw new NativeDialoguePromptCompositionException(
                    input.FilmProjectId,
                    input.Scene.Id,
                    input.Scene.SceneNumber,
                    NativeDialoguePromptFailureStage.ResponseValidation,
                    string.Join(" | ", nativePrompt.Warnings),
                    nativePrompt.DiagnosticPath);
            }

            prompt = nativePrompt.CombinedPrompt;
            dialogueHash = nativePrompt.DialogueSourceHash;
            nativeProfileIds = nativePrompt.CharacterVoiceProfileIds;
            nativeProfileHashes = nativePrompt.VoiceSettingsHashes;
            exactSpokenLines = nativePrompt.ExactSpokenLines;
            dialogueCount = nativePrompt.DialogueCount;
            speakerCount = nativePrompt.SpeakerCount;
            nativeSpeakerDisplayName = nativePrompt.SpeakerDisplayName;
            nativeVoiceDirection = nativePrompt.VoiceDirection;
            nativeVisualDirection = nativePrompt.VideoPrompt;
            otherCharacterDisplayNames = nativePrompt.OtherCharacterDisplayNames;
        }

        return new WanGpVideoGenerationRequest
        {
            FilmProjectId = input.FilmProjectId,
            SceneId = input.Scene.Id,
            SceneNumber = input.Scene.SceneNumber,
            SourceImageAssetId = input.SourceImageAsset.Id,
            SourceImagePath = input.SourceImageAsset.FilePath,
            ModelType = model.ModelType,
            Prompt = prompt,
            NegativePrompt = string.IsNullOrWhiteSpace(input.NegativePromptOverride)
                ? input.Scene.VideoNegativePrompt
                : input.NegativePromptOverride,
            Resolution = input.Resolution,
            DurationSeconds = requestedDurationSeconds,
            InferenceSteps = input.InferenceSteps,
            Seed = input.Seed,
            RandomSeed = input.RandomSeed,
            InputMode = "start",
            GenerationMode = generationMode,
            DialogueSourceHash = dialogueHash,
            ExactSpokenLines = exactSpokenLines,
            NativeSpeakerDisplayName = nativeSpeakerDisplayName,
            NativeVoiceDirection = nativeVoiceDirection,
            NativeVisualDirection = nativeVisualDirection,
            OtherCharacterDisplayNames = otherCharacterDisplayNames,
            CharacterVoiceProfileIds = nativeProfileIds,
            VoiceSettingsHashes = nativeProfileHashes,
            DialogueCount = dialogueCount,
            SpeakerCount = speakerCount,
            CanonicalModelType = capability.CanonicalModelType,
            NativeDialogueCapabilitySupported = generationMode != VideoAudioGenerationMode.LtxNativeDialogue || capability.IsSupported,
            NativeDialogueCapabilityFailureReason = capability.FailureReason,
            NativeDialogueCapabilityEvidence = capability.Evidence,
            StopOnFailure = true,
            InputContract = inputContract,
            SettingsPatch = input.SettingsPatch
        };
    }

    private async Task<WanGpModelInfo> ResolveModelAsync(string requestedModelType, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedModelType))
        {
            return new WanGpModelInfo
            {
                ModelType = requestedModelType,
                DisplayName = requestedModelType,
                Family = requestedModelType,
                Architecture = requestedModelType,
                BaseModelType = requestedModelType,
                Outputs = "video audio",
                Inputs = "image",
                SupportsImageToVideo = true,
                SupportsStartImage = true,
                SupportsReferenceImage = true,
                InstallStatus = WanGpModelInstallStatus.Installed,
                Availability = "installed"
            };
        }

        var models = await _wanGpClient.GetAvailableImageToVideoModelsAsync(cancellationToken);
        return models.FirstOrDefault(model => model.IsAvailable && model.ModelType.Contains("ltx2_22B_distilled_gguf_q4_k_m", StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => model.IsAvailable && model.ModelType.Contains("ltx", StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(model => model.IsAvailable && model.SupportsImageToVideo)
            ?? models.FirstOrDefault(model => model.SupportsImageToVideo)
            ?? throw new InvalidOperationException("WanGP image-to-video modeli bulunamadı.");
    }

    private static Dictionary<string, object?> ToObjectDictionary(System.Text.Json.Nodes.JsonObject json)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in json)
        {
            result[item.Key] = item.Value is null ? null : JsonSerializer.Deserialize<object>(item.Value.ToJsonString());
        }

        return result;
    }

    private static bool SceneHasDialogue(Models.FilmScene scene)
    {
        if (string.IsNullOrWhiteSpace(scene.DialogueJson))
        {
            return false;
        }

        var trimmed = scene.DialogueJson.Trim();
        return trimmed != "[]" && trimmed != "{}";
    }
}
