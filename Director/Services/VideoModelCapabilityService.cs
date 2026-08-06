using Director.Services.Interfaces;

namespace Director.Services;

public sealed class VideoModelCapabilityService : IVideoModelCapabilityService
{
    public const string VerifiedLtxModelType = "ltx2_22B_distilled_gguf_q4_k_m";
    public const int VerifiedLtxDurationSeconds = 10;

    public VideoModelCapability GetCapability(string modelType)
    {
        if (IsVerifiedLtx(modelType))
        {
            return new VideoModelCapability(
                VerifiedLtxModelType,
                [VerifiedLtxDurationSeconds],
                VerifiedLtxDurationSeconds,
                VerifiedLtxDurationSeconds,
                VerifiedLtxDurationSeconds,
                VideoDurationMode.Fixed,
                RequiresStartImage: true,
                SupportsNativeDialogue: true);
        }

        return new VideoModelCapability(
            modelType.Trim(),
            [VerifiedLtxDurationSeconds],
            VerifiedLtxDurationSeconds,
            VerifiedLtxDurationSeconds,
            VerifiedLtxDurationSeconds,
            VideoDurationMode.Fixed,
            RequiresStartImage: true,
            SupportsNativeDialogue: false);
    }

    public VideoDurationValidationResult ValidateDuration(string modelType, int durationSeconds)
    {
        var capability = GetCapability(modelType);
        if (capability.SupportsDuration(durationSeconds))
        {
            return new VideoDurationValidationResult(true, capability, durationSeconds, string.Empty);
        }

        var supported = string.Join(", ", capability.SupportedDurationsSeconds);
        return new VideoDurationValidationResult(
            false,
            capability,
            durationSeconds,
            $"Secili video modeli '{capability.CanonicalModelType}' {durationSeconds} saniyelik klip suresini desteklemiyor. Desteklenen sureler: {supported} saniye.");
    }

    public VideoDurationValidationResult ValidateSnapshot(string modelType, int clipDurationSeconds, int calculatedClipCount)
    {
        if (calculatedClipCount <= 0)
        {
            var capability = GetCapability(modelType);
            return new VideoDurationValidationResult(
                false,
                capability,
                clipDurationSeconds,
                "Otonom run snapshot gecersiz: CalculatedClipCount pozitif degil.");
        }

        return ValidateDuration(modelType, clipDurationSeconds);
    }

    private static bool IsVerifiedLtx(string modelType) =>
        modelType.Contains(VerifiedLtxModelType, StringComparison.OrdinalIgnoreCase) ||
        modelType.Contains("ltx2", StringComparison.OrdinalIgnoreCase);
}
