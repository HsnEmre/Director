using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpAudioRequestBuilder
{
    Task<WanGpAudioRequestBuildResult> BuildAsync(WanGpAudioGenerationRequest request, CancellationToken cancellationToken = default);
}
