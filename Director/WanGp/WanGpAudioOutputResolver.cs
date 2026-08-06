using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpAudioOutputResolver : IWanGpAudioOutputResolver
{
    private readonly IWanGpFinalOutputResolver _finalOutputResolver;

    public WanGpAudioOutputResolver(IWanGpFinalOutputResolver finalOutputResolver)
    {
        _finalOutputResolver = finalOutputResolver;
    }

    public WanGpOutputSnapshot CaptureSnapshot() =>
        _finalOutputResolver.CaptureSnapshot(WanGpOutputMediaKind.Audio);

    public async Task<WanGpOutputResolveResult> ResolveAudioOutputsAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IReadOnlyList<string> explicitPaths,
        TimeSpan? maxWait = null,
        string? externalJobId = null,
        int? jobId = null,
        int? sceneId = null,
        int? seed = null,
        DateTime? completedAt = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolution = await _finalOutputResolver.ResolveAsync(new WanGpFinalOutputResolveRequest
            {
                MediaKind = WanGpOutputMediaKind.Audio,
                BeforeSnapshot = beforeSnapshot,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ExplicitPaths = explicitPaths,
                ExternalJobId = externalJobId,
                JobId = jobId,
                SceneId = sceneId,
                Seed = seed,
                MaxWait = maxWait
            }, cancellationToken);

            return new WanGpOutputResolveResult
            {
                Success = true,
                Message = resolution.Message,
                Candidates = [resolution.Candidate]
            };
        }
        catch (WanGpAmbiguousOutputException ex)
        {
            return new WanGpOutputResolveResult
            {
                Success = false,
                IsAmbiguous = true,
                Message = ex.Message,
                Candidates = ex.Candidates.ToList()
            };
        }
    }
}
