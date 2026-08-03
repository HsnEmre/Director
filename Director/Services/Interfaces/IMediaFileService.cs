using Director.Models;
using Director.Enums;
using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IMediaFileService
{
    Task<SceneMediaAsset> CopyGeneratedImageAsync(
        FilmScene scene,
        GenerationJob job,
        WanGpJobSnapshot snapshot,
        int versionNumber,
        bool isSelected,
        CancellationToken cancellationToken = default);

    Task<SceneMediaAsset> CopyImageAsync(
        FilmScene scene,
        GenerationJob job,
        string sourcePath,
        int versionNumber,
        bool isSelected,
        int? seed = null,
        CancellationToken cancellationToken = default);

    Task<SceneMediaAsset> CopyGeneratedVideoAsync(
        FilmScene scene,
        GenerationJob job,
        string sourcePath,
        VideoMetadata metadata,
        int versionNumber,
        bool isSelected,
        int sourceImageAssetId,
        string? fallbackThumbnailPath = null,
        CancellationToken cancellationToken = default);

    Task<SceneMediaAsset> CopyGeneratedAudioAsync(
        FilmScene scene,
        GenerationJob job,
        string sourcePath,
        VideoMetadata metadata,
        int versionNumber,
        MediaAssetRole role,
        string metadataJson,
        CancellationToken cancellationToken = default);
}
