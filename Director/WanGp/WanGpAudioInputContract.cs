namespace Director.WanGp;

public sealed class WanGpAudioInputContract
{
    public string TextKey { get; set; } = string.Empty;
    public string VoiceKey { get; set; } = string.Empty;
    public string? LanguageKey { get; set; }
    public string SpeakerDialogueFormat { get; set; } = "SingleSegment";
    public string? SeedKey { get; set; }
    public string? CfgScaleKey { get; set; }
    public string? DoSampleKey { get; set; }
    public string? TemperatureKey { get; set; }
    public string? MaxNewTokensKey { get; set; }
    public string? OutputFormatKey { get; set; }
    public bool SupportsVoicePreset { get; set; }
    public bool UsesImplicitDefaultVoice { get; set; }
    public bool SupportsDeterministicGeneration { get; set; }
    public bool SupportsDialogue { get; set; }
    public bool SupportsRawReferenceAudio { get; set; }
    public List<WanGpVoicePreset> AvailableVoices { get; set; } = [];
    public List<string> Evidence { get; set; } = [];
    public bool IsValidated { get; set; }
    public string FailureReason { get; set; } = string.Empty;
}

public sealed class WanGpVoicePreset
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
