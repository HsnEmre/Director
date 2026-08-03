namespace Director.WanGp;

public enum WanGpVideoInputContractEvidence
{
    ModelMetadata,
    ModelSchema,
    DefaultSettings,
    WanGpModelDefinition,
    WanGpExportedSettings,
    ArchitectureCompatibilityProfile
}

public enum WanGpVideoStartImageValueShape
{
    StringPath,
    StringPathArray
}

public sealed class WanGpVideoInputContract
{
    public bool SupportsImageToVideo { get; set; }
    public bool SupportsStartImage { get; set; }
    public bool SupportsReferenceImage { get; set; }
    public string StartImageKey { get; set; } = string.Empty;
    public string StartImageModeKey { get; set; } = string.Empty;
    public string StartImageModeValue { get; set; } = string.Empty;
    public WanGpVideoStartImageValueShape StartImageValueShape { get; set; } = WanGpVideoStartImageValueShape.StringPath;
    public string ReferenceImageKey { get; set; } = string.Empty;
    public List<WanGpVideoInputContractEvidence> Evidence { get; set; } = [];
    public string ResolutionSource { get; set; } = string.Empty;
    public bool IsValidated { get; set; }
    public string FailureReason { get; set; } = string.Empty;

    public string EvidenceText => Evidence.Count == 0
        ? string.Empty
        : string.Join(", ", Evidence.Distinct());
}
