using Director.Enums;

namespace Director.Services.Interfaces;

public interface IGpuGenerationCoordinator
{
    bool IsBusy { get; }

    Task<IAsyncDisposable> AcquireAsync(
        GenerationOperationType operationType,
        int projectId,
        int sceneId,
        CancellationToken cancellationToken = default);
}
