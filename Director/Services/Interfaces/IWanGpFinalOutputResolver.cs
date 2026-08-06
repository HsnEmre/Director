using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpFinalOutputResolver
{
    WanGpOutputSnapshot CaptureSnapshot(WanGpOutputMediaKind mediaKind);

    Task<WanGpFinalOutputResolution> ResolveAsync(
        WanGpFinalOutputResolveRequest request,
        CancellationToken cancellationToken = default);
}
