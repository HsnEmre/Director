using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpVideoTimingContractResolver : IWanGpVideoTimingContractResolver
{
    public WanGpVideoTimingContract Resolve(WanGpModelSchema schema, int requestedDurationSeconds)
    {
        var defaults = schema.DefaultSettings;
        var raw = schema.RawSchema.ToJsonString() + defaults.ToJsonString();
        var fps = ReadDouble(defaults, 24, "fps", "force_fps", "video_fps");
        var duration = Math.Max(1, requestedDurationSeconds);
        var contract = new WanGpVideoTimingContract
        {
            RequestedDurationSeconds = duration,
            AppliedDurationSeconds = duration,
            DefaultFps = fps,
            SelectedFps = fps,
            CalculatedFrameCount = Math.Max(1, (int)Math.Round(duration * fps)),
            Evidence = ["ModelSchema", "DefaultSettings"]
        };

        if (ContainsKey(raw, "video_length"))
        {
            contract.DurationKey = "video_length";
            contract.FrameCountKey = "video_length";
            contract.DurationUnit = WanGpVideoDurationUnit.Frames;
        }
        else if (ContainsKey(raw, "num_frames"))
        {
            contract.DurationKey = "num_frames";
            contract.FrameCountKey = "num_frames";
            contract.DurationUnit = WanGpVideoDurationUnit.Frames;
        }
        else if (ContainsKey(raw, "duration_seconds"))
        {
            contract.DurationKey = "duration_seconds";
            contract.DurationUnit = WanGpVideoDurationUnit.Seconds;
        }
        else if (ContainsKey(raw, "duration"))
        {
            contract.DurationKey = "duration";
            contract.DurationUnit = WanGpVideoDurationUnit.Seconds;
        }

        if (ContainsKey(raw, "force_fps"))
        {
            contract.FpsKey = "force_fps";
        }
        else if (ContainsKey(raw, "fps"))
        {
            contract.FpsKey = "fps";
        }

        if (raw.Contains("ltx2", StringComparison.OrdinalIgnoreCase) || schema.ModelType.Contains("ltx", StringComparison.OrdinalIgnoreCase))
        {
            contract.Evidence.Add("LtxCompatibilityProfile");
            if (string.IsNullOrWhiteSpace(contract.DurationKey))
            {
                contract.DurationKey = "video_length";
                contract.FrameCountKey = "video_length";
                contract.DurationUnit = WanGpVideoDurationUnit.Frames;
            }

            if (string.IsNullOrWhiteSpace(contract.FpsKey))
            {
                contract.FpsKey = "force_fps";
            }
        }

        contract.IsValidated = !string.IsNullOrWhiteSpace(contract.DurationKey);
        return contract;
    }

    private static bool ContainsKey(string json, string key) => json.Contains(key, StringComparison.OrdinalIgnoreCase);

    private static double ReadDouble(System.Text.Json.Nodes.JsonObject obj, double fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                return value;
            }
        }

        return fallback;
    }
}
