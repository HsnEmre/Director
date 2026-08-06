using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IVideoModelCapabilityService
{
    VideoModelCapability GetCapability(string modelType);
    VideoDurationValidationResult ValidateDuration(string modelType, int durationSeconds);
    VideoDurationValidationResult ValidateSnapshot(string modelType, int clipDurationSeconds, int calculatedClipCount);
}

public enum VideoDurationMode
{
    Fixed,
    Enumerated,
    Range
}

public sealed record VideoModelCapability(
    string CanonicalModelType,
    IReadOnlyList<int> SupportedDurationsSeconds,
    int DefaultDurationSeconds,
    int MinimumDurationSeconds,
    int MaximumDurationSeconds,
    VideoDurationMode DurationMode,
    bool RequiresStartImage,
    bool SupportsNativeDialogue)
{
    public bool SupportsDuration(int durationSeconds) =>
        DurationMode == VideoDurationMode.Range
            ? durationSeconds >= MinimumDurationSeconds && durationSeconds <= MaximumDurationSeconds
            : SupportedDurationsSeconds.Contains(durationSeconds);
}

public sealed record VideoDurationValidationResult(
    bool IsValid,
    VideoModelCapability Capability,
    int RequestedDurationSeconds,
    string ErrorMessage);
