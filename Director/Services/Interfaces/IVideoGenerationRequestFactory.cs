using Director.Dtos.MediaGeneration;
using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IVideoGenerationRequestFactory
{
    Task<WanGpVideoGenerationRequest> CreateAsync(
        VideoGenerationRequestFactoryInput input,
        CancellationToken cancellationToken = default);
}
