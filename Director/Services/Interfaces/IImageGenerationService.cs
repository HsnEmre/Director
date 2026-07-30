using Director.Dtos.MediaGeneration;
using Director.Models;
using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IImageGenerationService
{
    Task<GenerationJob> GenerateSceneImageAsync(
        int sceneId,
        WanGpImageGenerationRequest request,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task GenerateMissingImagesAsync(
        int filmProjectId,
        WanGpImageGenerationRequest templateRequest,
        bool stopOnError,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task CancelActiveJobAsync(CancellationToken cancellationToken = default);
    Task SetSelectedAssetAsync(int assetId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset> ImportExistingWanGpOutputAsync(int sceneId, string sourcePath, bool makeSelected = true, CancellationToken cancellationToken = default);
    Task MarkOrphanRunningJobsInterruptedAsync(CancellationToken cancellationToken = default);
}
