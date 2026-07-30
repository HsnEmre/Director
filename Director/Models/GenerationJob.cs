using Director.Enums;

namespace Director.Models;

public sealed class GenerationJob
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;
    public int SceneId { get; set; }
    public FilmScene Scene { get; set; } = null!;
    public int? SourceMediaAssetId { get; set; }
    public SceneMediaAsset? SourceMediaAsset { get; set; }
    public MediaType MediaType { get; set; }
    public GenerationProvider Provider { get; set; }
    public string? ExternalJobId { get; set; }
    public GenerationJobStatus Status { get; set; }
    public string ModelType { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public string SettingsJson { get; set; } = "{}";
    public double ProgressPercentage { get; set; }
    public string CurrentPhase { get; set; } = string.Empty;
    public int? CurrentStep { get; set; }
    public int? TotalSteps { get; set; }
    public string? ErrorMessage { get; set; }
    public string? PromptPreparationModel { get; set; }
    public DateTime? PromptPreparedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelRequestedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<SceneMediaAsset> Assets { get; set; } = new List<SceneMediaAsset>();
}
