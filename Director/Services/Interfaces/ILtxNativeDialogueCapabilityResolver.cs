using Director.WanGp;

namespace Director.Services.Interfaces;

public interface ILtxNativeDialogueCapabilityResolver
{
    LtxNativeDialogueCapability Resolve(
        WanGpModelInfo model,
        WanGpVideoInputContract? inputContract,
        WanGpModelInstallStatus installStatus,
        WanGpVideoRequestBuildResult? requestBuild = null);
}

public sealed class LtxNativeDialogueCapability
{
    public bool IsLtxFamily { get; set; }
    public bool SupportsVideoOutput { get; set; }
    public bool SupportsAudioOutput { get; set; }
    public bool SupportsImageToVideo { get; set; }
    public bool SupportsStartImage { get; set; }
    public bool IsInstalled { get; set; }
    public bool InputContractValidated { get; set; }
    public bool NativeAudioContractValidated { get; set; }
    public string CanonicalModelType { get; set; } = string.Empty;
    public List<string> Evidence { get; set; } = [];
    public bool IsSupported { get; set; }
    public string FailureReason { get; set; } = string.Empty;
}
