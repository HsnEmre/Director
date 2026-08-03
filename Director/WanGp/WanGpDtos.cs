using System.Text.Json.Nodes;
using Director.Enums;

namespace Director.WanGp;

public sealed class WanGpConnectionResult
{
    public bool IsAvailable { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class WanGpModelInfo
{
    public string ModelType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public string MainOutput { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string BaseModelType { get; set; } = string.Empty;
    public string Outputs { get; set; } = string.Empty;
    public string Inputs { get; set; } = string.Empty;
    public bool SupportsImageToVideo { get; set; }
    public bool SupportsStartImage { get; set; }
    public bool SupportsReferenceImage { get; set; }
    public JsonObject RawMetadata { get; set; } = new();
    public WanGpModelInstallStatus InstallStatus { get; set; } = WanGpModelInstallStatus.Unknown;
    public string? CheckpointPath { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public bool IsAvailable => InstallStatus == WanGpModelInstallStatus.Installed ||
        string.Equals(Availability, "available", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Availability, "installed", StringComparison.OrdinalIgnoreCase);
}

public sealed class WanGpModelSchema
{
    public string ModelType { get; set; } = string.Empty;
    public JsonObject RawSchema { get; set; } = new();
    public JsonObject DefaultSettings { get; set; } = new();
    public List<string> SupportedResolutions { get; set; } = new();
    public bool SupportsNegativePrompt { get; set; }
    public bool SupportsSeed { get; set; }
    public bool SupportsImageInput { get; set; }
    public int DefaultInferenceSteps { get; set; } = 20;
}

public sealed class WanGpGenerationSubmission
{
    public string ExternalJobId { get; set; } = string.Empty;
    public JsonObject RawResponse { get; set; } = new();
}

public sealed class WanGpJobSnapshot
{
    public string ExternalJobId { get; set; } = string.Empty;
    public GenerationJobStatus Status { get; set; }
    public double ProgressPercentage { get; set; }
    public string Phase { get; set; } = string.Empty;
    public int? CurrentStep { get; set; }
    public int? TotalSteps { get; set; }
    public string? Message { get; set; }
    public string? OutputPath { get; set; }
    public List<string> GeneratedFiles { get; set; } = [];
    public int? Seed { get; set; }
}

public sealed class WanGpImageGenerationRequest
{
    public string ModelType { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public int InferenceSteps { get; set; }
    public int? Seed { get; set; }
    public bool RandomSeed { get; set; }
    public bool StopOnError { get; set; }
}

public sealed class WanGpVideoGenerationRequest
{
    public int FilmProjectId { get; set; }
    public int SceneId { get; set; }
    public int SourceImageAssetId { get; set; }
    public string SourceImagePath { get; set; } = string.Empty;
    public string ModelType { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public int? FrameCount { get; set; }
    public double? Fps { get; set; }
    public int InferenceSteps { get; set; }
    public double? GuidanceScale { get; set; }
    public int? Seed { get; set; }
    public bool RandomSeed { get; set; }
    public string InputMode { get; set; } = "start";
    public VideoAudioGenerationMode GenerationMode { get; set; } = VideoAudioGenerationMode.SilentVideo;
    public string DialogueSourceHash { get; set; } = string.Empty;
    public List<string> ExactSpokenLines { get; set; } = [];
    public List<int> CharacterVoiceProfileIds { get; set; } = [];
    public List<string> VoiceSettingsHashes { get; set; } = [];
    public int DialogueCount { get; set; }
    public int SpeakerCount { get; set; }
    public string CanonicalModelType { get; set; } = string.Empty;
    public bool NativeDialogueCapabilitySupported { get; set; }
    public string NativeDialogueCapabilityFailureReason { get; set; } = string.Empty;
    public List<string> NativeDialogueCapabilityEvidence { get; set; } = [];
    public bool StopOnFailure { get; set; }
    public WanGpVideoInputContract? InputContract { get; set; }
    public Dictionary<string, object?> SettingsPatch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
