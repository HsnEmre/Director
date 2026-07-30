using System.IO;
using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpVideoRequestBuilder : IWanGpVideoRequestBuilder
{
    private readonly IWanGpClient _wanGpClient;

    public WanGpVideoRequestBuilder(IWanGpClient wanGpClient)
    {
        _wanGpClient = wanGpClient;
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

        var schemaJson = schema.RawSchema.ToJsonString() + schema.DefaultSettings.ToJsonString();
        var imageKey = FindKey(schemaJson, "start_image", "image_start", "init_image", "source_image", "input_image")
            ?? FindKey(schemaJson, "reference_image", "ref_image", "image");
        var supportsStart = imageKey is not null && imageKey.Contains("start", StringComparison.OrdinalIgnoreCase);
        var supportsReference = imageKey is not null && !supportsStart;
        if (imageKey is null)
        {
            throw new InvalidOperationException("Secili model start/reference image alani gostermiyor; image-to-video icin secilemez.");
        }

        var source = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model_type"] = request.ModelType,
            ["prompt"] = request.Prompt,
            [imageKey] = request.SourceImagePath
        };

        AddIfSupported(source, schemaJson, request.Resolution, "resolution", "size", "video_resolution");
        AddIfSupported(source, schemaJson, request.InferenceSteps, "num_inference_steps", "inference_steps", "steps");

        var negativeKey = FindKey(schemaJson, "negative_prompt", "negativePrompt");
        if (!string.IsNullOrWhiteSpace(negativeKey) && !string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            source[negativeKey] = request.NegativePrompt;
        }

        var durationKey = AddIfSupported(source, schemaJson, request.DurationSeconds, "duration", "duration_seconds", "video_duration", "clip_duration");
        var fpsKey = request.Fps is double fps ? AddIfSupported(source, schemaJson, fps, "fps", "video_fps") : null;
        var frameKey = request.FrameCount is int frames ? AddIfSupported(source, schemaJson, frames, "frame_count", "num_frames", "frames", "video_length") : null;

        if (!request.RandomSeed && request.Seed is int seed)
        {
            AddIfSupported(source, schemaJson, seed, "seed");
        }

        if (request.GuidanceScale is double guidance)
        {
            AddIfSupported(source, schemaJson, guidance, "guidance_scale", "cfg_scale");
        }

        foreach (var patch in request.SettingsPatch)
        {
            if (FindKey(schemaJson, patch.Key) is not null)
            {
                source[patch.Key] = patch.Value;
            }
        }

        if (source.Keys.Any(key => key.Contains("image_mode", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Video request image_mode alani iceremez.");
        }

        return new WanGpVideoRequestBuildResult
        {
            Source = source,
            Schema = schema,
            SupportsNegativePrompt = negativeKey is not null,
            SupportsStartImage = supportsStart,
            SupportsReferenceImage = supportsReference,
            SupportsDurationSeconds = durationKey is not null,
            SupportsFps = fpsKey is not null,
            SupportsFrameCount = frameKey is not null,
            ImageInputKey = imageKey
        };
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
}
