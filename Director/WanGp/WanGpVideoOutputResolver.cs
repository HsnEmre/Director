using Director.Services.Interfaces;

namespace Director.WanGp;

public sealed class WanGpVideoOutputResolver : IWanGpVideoOutputResolver
{
    private readonly IWanGpFinalOutputResolver _finalOutputResolver;

    public WanGpVideoOutputResolver(IWanGpFinalOutputResolver finalOutputResolver)
    {
        _finalOutputResolver = finalOutputResolver;
    }

    public WanGpOutputSnapshot CaptureSnapshot() =>
        _finalOutputResolver.CaptureSnapshot(WanGpOutputMediaKind.Video);

    public async Task<WanGpOutputResolveResult> ResolveVideoOutputsAsync(
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolution = await _finalOutputResolver.ResolveAsync(new WanGpFinalOutputResolveRequest
            {
                MediaKind = WanGpOutputMediaKind.Video,
                BeforeSnapshot = beforeSnapshot,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                ExplicitPaths = explicitPaths,
                ExternalJobId = externalJobId,
                JobId = jobId,
                SceneId = sceneId,
                Seed = seed,
                RequireAudio = requireAudio,
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
