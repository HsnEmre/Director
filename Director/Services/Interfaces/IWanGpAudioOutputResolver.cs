using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpAudioOutputResolver
{
    WanGpOutputSnapshot CaptureSnapshot();
    Task<WanGpOutputResolveResult> ResolveAudioOutputsAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IReadOnlyList<string> explicitPaths,
        TimeSpan? maxWait = null,
        string? externalJobId = null,
        int? jobId = null,
        int? sceneId = null,
        int? seed = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default);
}
