using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpVideoOutputResolver
{
    WanGpOutputSnapshot CaptureSnapshot();

    Task<WanGpOutputResolveResult> ResolveVideoOutputsAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IReadOnlyList<string> explicitPaths,
        TimeSpan? maxWait = null,
        CancellationToken cancellationToken = default);
}
