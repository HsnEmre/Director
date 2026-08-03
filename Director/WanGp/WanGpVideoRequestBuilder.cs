using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpVideoRequestBuilder : IWanGpVideoRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpVideoInputContractResolver _inputContractResolver;
    private readonly IWanGpVideoTimingContractResolver _timingContractResolver;

    public WanGpVideoRequestBuilder(
        IWanGpClient wanGpClient,
        IWanGpVideoInputContractResolver inputContractResolver,
        IWanGpVideoTimingContractResolver timingContractResolver)
    {
        _wanGpClient = wanGpClient;
        _inputContractResolver = inputContractResolver;
        _timingContractResolver = timingContractResolver;
    }

    public async Task<WanGpVideoRequestBuildResult> BuildAsync(WanGpVideoGenerationRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ModelType.Contains("qwen_image", StringComparison.OrdinalIgnoreCase) ||
            request.ModelType.Contains("qwen image", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Qwen Image video uretim modeli olarak kullanilamaz.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceImagePath) || !File.Exists(request.SourceImagePath))
        {
            throw new FileNotFoundException("Video referans gorseli bulunamadi.", request.SourceImagePath);
        }

        var schema = await _wanGpClient.GetModelSchemaAsync(request.ModelType, cancellationToken)
            ?? throw new InvalidOperationException("WanGP video model schema alinamadi.");

        WriteSchemaDiagnostics(request.ModelType, schema);

        var sourceImagePath = Path.GetFullPath(request.SourceImagePath);
        var sourceInfo = new FileInfo(sourceImagePath);
        if (sourceInfo.Length <= 0)
        {
            throw new InvalidOperationException("Video referans gorseli bos dosya.");
        }

        var defaults = ToObjectDictionary(schema.DefaultSettings);
        var contract = request.InputContract ?? await _inputContractResolver.ResolveAsync(new WanGpModelInfo
        {
            ModelType = request.ModelType,
            SupportsImageToVideo = true,
            SupportsStartImage = request.ModelType.Contains("ltx", StringComparison.OrdinalIgnoreCase),
            SupportsReferenceImage = request.ModelType.Contains("ltx", StringComparison.OrdinalIgnoreCase),
            Inputs = "image",
            Outputs = "video",
            RawMetadata = new JsonObject
            {
                ["model_type"] = request.ModelType,
                ["capabilities"] = new JsonObject { ["image_to_video"] = true },
                ["media_inputs"] = new JsonObject { ["image"] = new JsonObject { ["start"] = true, ["reference"] = true } }
            }
        }, schema, defaults, cancellationToken);

        if (!contract.IsValidated)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(contract.FailureReason)
                ? "Secili model image-to-video destekliyor ancak WanGP Start Image sozlesmesi cozumlenemedi."
                : contract.FailureReason);
        }

        var schemaJson = schema.RawSchema.ToJsonString() + schema.DefaultSettings.ToJsonString();
        var imageKey = contract.StartImageKey;
        var inputModeKey = contract.StartImageModeKey;
        var inputModeValue = contract.StartImageModeValue;
        object startImageValue = contract.StartImageValueShape == WanGpVideoStartImageValueShape.StringPathArray
            ? new[] { sourceImagePath }
            : sourceImagePath;
        var source = new Dictionary<string, object?>(defaults, StringComparer.OrdinalIgnoreCase)
        {
            ["model_type"] = request.ModelType
        };

        foreach (var patch in request.SettingsPatch)
        {
            if (IsPromptKey(patch.Key))
            {
                continue;
            }

            if (FindKey(schemaJson, patch.Key) is not null)
            {
                source[patch.Key] = patch.Value;
            }
        }

        AddIfSupported(source, schemaJson, request.Resolution, "resolution", "size", "video_resolution");
        AddIfSupported(source, schemaJson, request.InferenceSteps, "num_inference_steps", "inference_steps", "steps");
        var timing = _timingContractResolver.Resolve(schema, request.DurationSeconds);
        ApplyTiming(source, timing);

        var negativeKey = FindKey(schemaJson, "negative_prompt", "negativePrompt");
        if (!string.IsNullOrWhiteSpace(negativeKey) && !string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            source[negativeKey] = request.NegativePrompt;
        }

        var durationKey = timing.DurationKey;
        var fpsKey = timing.FpsKey;
        var frameKey = timing.FrameCountKey;

        if (!request.RandomSeed && request.Seed is int seed)
        {
            AddIfSupported(source, schemaJson, seed, "seed");
        }

        if (request.GuidanceScale is double guidance)
        {
            AddIfSupported(source, schemaJson, guidance, "guidance_scale", "cfg_scale");
        }

        source[imageKey] = startImageValue;
        source[inputModeKey] = inputModeValue;

        if (request.GenerationMode == Director.Enums.VideoAudioGenerationMode.LtxNativeDialogue)
        {
            ValidateNativeDialoguePrompt(request);
            AssertNativeAudioNotDisabled(source, schemaJson);
        }

        source["prompt"] = request.Prompt;

        if (!TryReadStartImagePath(source, imageKey, out var resolvedStartImagePath) ||
            !File.Exists(resolvedStartImagePath))
        {
            throw new InvalidOperationException("LTX image-to-video request does not contain a start image.");
        }

        WriteRequestSummary(request, contract, timing, sourceInfo, source);

        return new WanGpVideoRequestBuildResult
        {
            Source = source,
            Schema = schema,
            SupportsNegativePrompt = negativeKey is not null,
            SupportsStartImage = true,
            SupportsReferenceImage = schemaJson.Contains("reference", StringComparison.OrdinalIgnoreCase),
            SupportsDurationSeconds = durationKey is not null,
            SupportsFps = fpsKey is not null,
            SupportsFrameCount = frameKey is not null,
            ImageInputKey = imageKey,
            InputModeKey = inputModeKey,
            InputModeValue = inputModeValue,
            InputContract = contract,
            TimingContract = timing,
            NativeAudioRequired = request.GenerationMode == Director.Enums.VideoAudioGenerationMode.LtxNativeDialogue,
            NativeAudioDisabledByRequest = request.GenerationMode == Director.Enums.VideoAudioGenerationMode.LtxNativeDialogue && IsNativeAudioDisabled(source)
        };
    }

    private static void AssertNativeAudioNotDisabled(IReadOnlyDictionary<string, object?> source, string schemaJson)
    {
        foreach (var key in new[] { "audio", "enable_audio", "audio_output", "generate_audio", "disable_audio", "mute", "no_audio" })
        {
            if (!schemaJson.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (source.TryGetValue(key, out var value) && IsAudioOffKeyValue(key, value))
            {
                throw new InvalidOperationException($"LTX native dialogue request audio output'u kapatiyor: {key}={value}");
            }
        }
    }

    private static void ValidateNativeDialoguePrompt(WanGpVideoGenerationRequest request)
    {
        if (request.DialogueCount <= 0 || string.IsNullOrWhiteSpace(request.DialogueSourceHash) || string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
        }

        if (!request.Prompt.Contains("speaks audibly in natural Turkish", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("clear Turkish pronunciation", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("synchronized lip movement", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("No narrator", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("No subtitles", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("No background music", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("No additional dialogue", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("No captions", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("No on-screen text", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("single continuous shot", StringComparison.OrdinalIgnoreCase) ||
            !request.Prompt.Contains("no cuts", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
        }

        if (request.ExactSpokenLines.Count > 0 && !request.ExactSpokenLines.Any(line => ContainsQuotedLine(request.Prompt, line)))
        {
            throw new InvalidOperationException("Native dialogue prompt olusturulamadi.");
        }
    }

    private static bool IsNativeAudioDisabled(IReadOnlyDictionary<string, object?> source)
    {
        return source.Any(pair => IsAudioOffKeyValue(pair.Key, pair.Value));
    }

    private static bool IsAudioOffKeyValue(string key, object? value)
    {
        var normalizedKey = key.ToLowerInvariant();
        var normalizedValue = value?.ToString()?.ToLowerInvariant() ?? string.Empty;
        if ((normalizedKey.Contains("disable_audio") || normalizedKey.Contains("no_audio") || normalizedKey.Contains("mute")) &&
            normalizedValue is "true" or "1" or "yes")
        {
            return true;
        }

        if ((normalizedKey.Contains("enable_audio") || normalizedKey.Contains("audio_output") || normalizedKey == "audio") &&
            normalizedValue is "false" or "0" or "none" or "off")
        {
            return true;
        }

        return false;
    }

    private static bool IsPromptKey(string key)
    {
        return key.Contains("prompt", StringComparison.OrdinalIgnoreCase);
    }

    private static string? AddIfSupported(Dictionary<string, object?> source, string schemaJson, object? value, params string[] keys)
    {
        var key = FindKey(schemaJson, keys);
        if (key is not null && value is not null)
        {
            source[key] = value;
        }

        return key;
    }

    private static string? FindKey(string schemaJson, params string[] keys)
    {
        return keys.FirstOrDefault(key => schemaJson.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyTiming(Dictionary<string, object?> source, WanGpVideoTimingContract timing)
    {
        if (!timing.IsValidated)
        {
            return;
        }

        source[timing.DurationKey] = timing.DurationUnit switch
        {
            WanGpVideoDurationUnit.Frames => timing.CalculatedFrameCount,
            WanGpVideoDurationUnit.Milliseconds => timing.AppliedDurationSeconds * 1000,
            _ => timing.AppliedDurationSeconds
        };

        if (!string.IsNullOrWhiteSpace(timing.FpsKey))
        {
            source[timing.FpsKey] = timing.SelectedFps;
        }
    }

    private static void WriteSchemaDiagnostics(string modelType, WanGpModelSchema schema)
    {
        if (!modelType.Contains("ltx", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var root = GetDiagnosticsRoot();
        File.WriteAllText(Path.Combine(root, "ltx-video-schema.json"), Sanitize(schema.RawSchema).ToJsonString(JsonOptions));
        File.WriteAllText(Path.Combine(root, "ltx-video-defaults.json"), Sanitize(schema.DefaultSettings).ToJsonString(JsonOptions));
    }

    private static JsonObject Sanitize(JsonObject source)
    {
        var clone = source.DeepClone() as JsonObject ?? new JsonObject();
        RemovePromptLikeValues(clone);
        return clone;
    }

    private static void RemovePromptLikeValues(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                if (key.Contains("prompt", StringComparison.OrdinalIgnoreCase) && obj[key] is JsonValue)
                {
                    obj[key] = "[redacted]";
                }
                else
                {
                    RemovePromptLikeValues(obj[key]);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                RemovePromptLikeValues(item);
            }
        }
    }

    private static void WriteRequestSummary(
        WanGpVideoGenerationRequest request,
        WanGpVideoInputContract contract,
        WanGpVideoTimingContract timing,
        FileInfo sourceInfo,
        IReadOnlyDictionary<string, object?> source)
    {
        var sentPrompt = source.TryGetValue("prompt", out var promptValue) ? promptValue?.ToString() ?? string.Empty : string.Empty;
        var summary = new JsonObject
        {
            ["capturedAt"] = DateTime.Now,
            ["modelType"] = request.ModelType,
            ["sceneId"] = request.SceneId,
            ["sourceImageAssetId"] = request.SourceImageAssetId,
            ["sourceImageFilename"] = sourceInfo.Name,
            ["sourceImagePathHash"] = Hash(sourceInfo.FullName),
            ["sourceImageByteSize"] = sourceInfo.Length,
            ["inputMode"] = "StartImage",
            ["resolvedInputModeKey"] = contract.StartImageModeKey,
            ["resolvedInputModeValue"] = contract.StartImageModeValue,
            ["resolvedStartImageKey"] = contract.StartImageKey,
            ["startImageValueShape"] = contract.StartImageValueShape.ToString(),
            ["inputContractValidated"] = contract.IsValidated,
            ["inputContractEvidence"] = contract.EvidenceText,
            ["startImagePresent"] = true,
            ["requestMediaType"] = "Video",
            ["generationMode"] = request.GenerationMode.ToString(),
            ["nativeAudioRequired"] = request.GenerationMode == Director.Enums.VideoAudioGenerationMode.LtxNativeDialogue,
            ["dialogueSourceHash"] = string.IsNullOrWhiteSpace(request.DialogueSourceHash) ? null : request.DialogueSourceHash,
            ["sentPromptHash"] = string.IsNullOrWhiteSpace(sentPrompt) ? null : Hash(sentPrompt),
            ["sentPromptIsRequestPrompt"] = string.Equals(sentPrompt, request.Prompt, StringComparison.Ordinal),
            ["combinedPromptContainsTurkishDialogue"] = ContainsTurkishText(sentPrompt),
            ["combinedPromptContainsSpeaksAudibly"] = sentPrompt.Contains("speaks audibly", StringComparison.OrdinalIgnoreCase),
            ["combinedPromptContainsQuotedTurkishLine"] = request.ExactSpokenLines.Count > 0 && request.ExactSpokenLines.Any(line => ContainsQuotedLine(sentPrompt, line)),
            ["dialogueCount"] = request.DialogueCount,
            ["speakerCount"] = request.SpeakerCount,
            ["resolution"] = request.Resolution,
            ["projectClipDurationSeconds"] = request.DurationSeconds,
            ["requestedDurationSeconds"] = timing.RequestedDurationSeconds,
            ["appliedDurationSeconds"] = timing.AppliedDurationSeconds,
            ["fps"] = timing.SelectedFps,
            ["frameCount"] = timing.CalculatedFrameCount,
            ["wanGpDurationKey"] = timing.DurationKey,
            ["wanGpDurationValue"] = timing.DurationUnit == WanGpVideoDurationUnit.Frames ? timing.CalculatedFrameCount : timing.AppliedDurationSeconds,
            ["wanGpDurationUnit"] = timing.DurationUnit.ToString(),
            ["steps"] = request.InferenceSteps,
            ["seed"] = request.RandomSeed ? null : request.Seed
        };

        File.WriteAllText(Path.Combine(GetDiagnosticsRoot(), "video-request-summary.json"), summary.ToJsonString(JsonOptions));
    }

    private static Dictionary<string, object?> ToObjectDictionary(JsonObject source)
    {
        return source.ToDictionary(
            item => item.Key,
            item => item.Value is null ? null : item.Value.Deserialize<object>(JsonOptions),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasDefaultKey(IReadOnlyDictionary<string, object?> defaults, string key)
    {
        return defaults.Keys.Any(item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryReadStartImagePath(IReadOnlyDictionary<string, object?> source, string imageKey, out string path)
    {
        path = string.Empty;
        if (!source.TryGetValue(imageKey, out var value) || value is null)
        {
            return false;
        }

        if (value is string single)
        {
            path = single;
            return !string.IsNullOrWhiteSpace(path);
        }

        if (value is IEnumerable<string> strings)
        {
            path = strings.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(path);
        }

        if (value is JsonArray array)
        {
            path = array.Select(item => item?.ToString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(path);
        }

        return false;
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static bool ContainsQuotedLine(string prompt, string line)
    {
        return !string.IsNullOrWhiteSpace(line) &&
            prompt.Contains($"\"{line}\"", StringComparison.Ordinal);
    }

    private static bool ContainsTurkishText(string value)
    {
        return value.Any(ch => "çğıöşüÇĞİÖŞÜ".Contains(ch));
    }

    private static string GetDiagnosticsRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorWanGpVideoDiagnostics");
        Directory.CreateDirectory(root);
        return root;
    }
}
