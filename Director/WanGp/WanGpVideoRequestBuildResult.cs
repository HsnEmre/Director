namespace Director.WanGp;

public sealed class WanGpVideoRequestBuildResult
{
    public Dictionary<string, object?> Source { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public WanGpModelSchema Schema { get; set; } = new();
    public bool SupportsNegativePrompt { get; set; }
    public bool SupportsStartImage { get; set; }
    public bool SupportsReferenceImage { get; set; }
    public bool SupportsDurationSeconds { get; set; }
    public bool SupportsFps { get; set; }
    public bool SupportsFrameCount { get; set; }
    public string ImageInputKey { get; set; } = string.Empty;
    public string InputModeKey { get; set; } = string.Empty;
    public string InputModeValue { get; set; } = string.Empty;
    public WanGpVideoInputContract? InputContract { get; set; }
    public WanGpVideoTimingContract? TimingContract { get; set; }
    public bool NativeAudioRequired { get; set; }
    public bool NativeAudioDisabledByRequest { get; set; }
    public bool HasStartImage => !string.IsNullOrWhiteSpace(ImageInputKey) &&
        Source.TryGetValue(ImageInputKey, out var value) &&
        value is not null &&
        !string.IsNullOrWhiteSpace(value.ToString());
}
