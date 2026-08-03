using Director.Models;

namespace Director.Services.Interfaces;

public interface IFinalDialogueVideoMuxingService
{
    Task<SceneMediaAsset> CreateFinalDialogueVideoAsync(int videoAssetId, int speechTrackAssetId, CancellationToken cancellationToken = default);
}
