using Director.Dtos.MediaGeneration;
using Director.Models;
using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IVideoGenerationService
{
    Task<GenerationJob> GenerateSceneVideoAsync(
        WanGpVideoGenerationRequest request,
        IProgress<MediaGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task CancelActiveJobAsync(CancellationToken cancellationToken = default);
    Task SetSelectedVideoAssetAsync(int assetId, CancellationToken cancellationToken = default);
}
