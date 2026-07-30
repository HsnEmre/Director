using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpVideoRequestBuilder
{
    Task<WanGpVideoRequestBuildResult> BuildAsync(WanGpVideoGenerationRequest request, CancellationToken cancellationToken = default);
}
