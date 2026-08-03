using Director.Enums;
using Director.Services.Interfaces;
using Director.WanGp;

namespace Director.Services;

public sealed class LtxNativeDialogueCapabilityResolver : ILtxNativeDialogueCapabilityResolver
{
    public const string VerifiedCanonicalModelType = "ltx2_22B_distilled_gguf_q4_k_m";

    public LtxNativeDialogueCapability Resolve(
        WanGpModelInfo model,
        WanGpVideoInputContract? inputContract,
        WanGpModelInstallStatus installStatus,
        WanGpVideoRequestBuildResult? requestBuild = null)
    {
        var text = string.Join(" ", model.ModelType, model.DisplayName, model.Family, model.Architecture, model.BaseModelType);
        var outputs = model.Outputs ?? string.Empty;
        var raw = model.RawMetadata.ToJsonString();
        var result = new LtxNativeDialogueCapability
        {
            IsLtxFamily = Contains(text, "ltx2") || Contains(text, "ltx-2") || Contains(text, "ltx2_22b"),
            SupportsVideoOutput = Contains(outputs, "video") || Contains(raw, "video") || Contains(model.MainOutput, "video"),
            SupportsAudioOutput = Contains(outputs, "audio") || Contains(raw, "audio"),
            SupportsImageToVideo = model.SupportsImageToVideo || Contains(raw, "image_to_video"),
            SupportsStartImage = model.SupportsStartImage || inputContract?.SupportsStartImage == true,
            IsInstalled = installStatus == WanGpModelInstallStatus.Installed || model.IsAvailable,
            InputContractValidated = inputContract?.IsValidated == true,
            NativeAudioContractValidated = requestBuild is null || (requestBuild.NativeAudioRequired && !requestBuild.NativeAudioDisabledByRequest),
            CanonicalModelType = Canonicalize(model)
        };

        if (Contains(model.ModelType, VerifiedCanonicalModelType))
        {
            result.Evidence.Add("ExactModelType");
        }

        if (result.IsLtxFamily)
        {
            result.Evidence.Add("Ltx2FamilyOrArchitecture");
        }

        if (result.SupportsVideoOutput)
        {
            result.Evidence.Add("VideoOutput");
        }

        if (result.SupportsAudioOutput)
        {
            result.Evidence.Add("AudioOutput");
        }

        if (result.SupportsImageToVideo)
        {
            result.Evidence.Add("ImageToVideo");
        }

        if (result.SupportsStartImage)
        {
            result.Evidence.Add("StartImage");
        }

        if (result.IsInstalled)
        {
            result.Evidence.Add("Installed");
        }

        if (result.InputContractValidated)
        {
            result.Evidence.Add("InputContractValidated");
        }

        if (result.NativeAudioContractValidated)
        {
            result.Evidence.Add("NativeAudioNotDisabled");
        }

        var failures = new List<string>();
        if (!result.IsLtxFamily) failures.Add("family/architecture LTX2 degil");
        if (!result.SupportsVideoOutput) failures.Add("video output yok");
        if (!result.SupportsAudioOutput) failures.Add("audio output yok");
        if (!result.SupportsImageToVideo) failures.Add("image-to-video yok");
        if (!result.SupportsStartImage) failures.Add("start image yok");
        if (!result.IsInstalled) failures.Add("checkpoint kurulu degil");
        if (!result.InputContractValidated) failures.Add("input contract dogrulanmadi");
        if (!result.NativeAudioContractValidated) failures.Add("native audio request tarafinda dogrulanmadi");
        result.IsSupported = failures.Count == 0;
        result.FailureReason = string.Join("; ", failures);
        return result;
    }

    private static string Canonicalize(WanGpModelInfo model)
    {
        return Contains(model.ModelType, VerifiedCanonicalModelType)
            ? VerifiedCanonicalModelType
            : model.ModelType;
    }

    private static bool Contains(string value, string term)
    {
        return value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
