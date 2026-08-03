using Director.Enums;

namespace Director.Models;

public sealed class SceneMediaAsset
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;
    public int SceneId { get; set; }
    public FilmScene Scene { get; set; } = null!;
    public int GenerationJobId { get; set; }
    public GenerationJob GenerationJob { get; set; } = null!;
    public int? SourceMediaAssetId { get; set; }
    public SceneMediaAsset? SourceMediaAsset { get; set; }
    public MediaType MediaType { get; set; }
    public MediaAssetRole Role { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? ThumbnailPath { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? Fps { get; set; }
    public int? FrameCount { get; set; }
    public int? Seed { get; set; }
    public string ModelType { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public int VersionNumber { get; set; }
    public bool IsSelected { get; set; }
    public DateTime CreatedAt { get; set; }
}
