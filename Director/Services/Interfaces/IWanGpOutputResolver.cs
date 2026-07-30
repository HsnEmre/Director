using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpOutputResolver
{
    WanGpOutputSnapshot CaptureSnapshot();

    Task<WanGpOutputResolveResult> ResolveImageOutputsAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IReadOnlyList<string> explicitPaths,
        TimeSpan? maxWait = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WanGpOutputCandidate>> ScanExistingImageOutputsAsync(CancellationToken cancellationToken = default);
}
