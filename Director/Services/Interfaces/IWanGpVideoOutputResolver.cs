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
        string? externalJobId = null,
        int? jobId = null,
        int? sceneId = null,
        int? seed = null,
        DateTime? completedAt = null,
        bool requireAudio = false,
        CancellationToken cancellationToken = default);
}
