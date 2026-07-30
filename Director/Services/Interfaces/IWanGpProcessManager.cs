using Director.Dtos.StoryGeneration;

namespace Director.Services.Interfaces;

public interface IWanGpProcessManager
{
    Task<bool> EnsureServerAsync(
        IProgress<GenerationLogEntry>? logs = null,
        CancellationToken cancellationToken = default);

    Task StopOwnedProcessAsync(CancellationToken cancellationToken = default);
}
