namespace Director.WanGp;

public sealed class WanGpAudioGenerationRequest
{
    public int FilmProjectId { get; set; }
    public int SceneId { get; set; }
    public int SpeechSegmentId { get; set; }
    public string ModelType { get; set; } = string.Empty;
    public string TurkishText { get; set; } = string.Empty;
    public string VoicePresetKey { get; set; } = string.Empty;
    public string Language { get; set; } = "tr";
    public string Emotion { get; set; } = string.Empty;
    public double? CfgScale { get; set; }
    public int? Seed { get; set; }
    public bool DoSample { get; set; }
    public double? Temperature { get; set; }
    public int? MaxNewTokens { get; set; }
    public double TargetDurationSeconds { get; set; }
    public WanGpAudioInputContract? InputContract { get; set; }
}

public sealed class WanGpAudioRequestBuildResult
{
    public Dictionary<string, object?> Source { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public WanGpAudioInputContract Contract { get; set; } = new();
    public string TextHash { get; set; } = string.Empty;
}
