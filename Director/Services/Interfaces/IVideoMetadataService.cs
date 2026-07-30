using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IVideoMetadataService
{
    Task<VideoMetadata> ProbeAsync(string videoPath, CancellationToken cancellationToken = default);
}
