using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpAudioOutputResolver
{
    WanGpOutputSnapshot CaptureSnapshot();
    Task<WanGpOutputResolveResult> ResolveAudioOutputsAsync(WanGpOutputSnapshot beforeSnapshot, DateTime startedAt, IReadOnlyList<string> explicitPaths, TimeSpan? maxWait = null, CancellationToken cancellationToken = default);
}
