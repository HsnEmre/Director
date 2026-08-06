using Director.Models;

namespace Director.Dtos.MediaGeneration;

public sealed class VideoGenerationRequestFactoryInput
{
    public int FilmProjectId { get; set; }
    public FilmScene Scene { get; set; } = null!;
    public SceneMediaAsset SourceImageAsset { get; set; } = null!;
    public string ModelType { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public int InferenceSteps { get; set; } = 30;
    public int? Seed { get; set; }
    public bool RandomSeed { get; set; } = true;
    public bool PreferNativeDialogue { get; set; } = true;
    public string? PromptOverride { get; set; }
    public string? NegativePromptOverride { get; set; }
    public Dictionary<string, object?> SettingsPatch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
