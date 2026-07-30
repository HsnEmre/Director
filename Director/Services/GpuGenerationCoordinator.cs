using Director.Enums;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class GpuGenerationCoordinator : IGpuGenerationCoordinator
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<GpuGenerationCoordinator> _logger;
    private int _isBusy;

    public GpuGenerationCoordinator(ILogger<GpuGenerationCoordinator> logger)
    {
        _logger = logger;
    }

    public bool IsBusy => Volatile.Read(ref _isBusy) == 1;

    public async Task<IAsyncDisposable> AcquireAsync(
        GenerationOperationType operationType,
        int projectId,
        int sceneId,
        CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        Volatile.Write(ref _isBusy, 1);
        _logger.LogInformation("GPU generation lock acquired. Operation={OperationType}; ProjectId={ProjectId}; SceneId={SceneId}", operationType, projectId, sceneId);
        return new Lease(this, operationType, projectId, sceneId);
    }

    private void Release(GenerationOperationType operationType, int projectId, int sceneId)
    {
        Volatile.Write(ref _isBusy, 0);
        _semaphore.Release();
        _logger.LogInformation("GPU generation lock released. Operation={OperationType}; ProjectId={ProjectId}; SceneId={SceneId}", operationType, projectId, sceneId);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly GpuGenerationCoordinator _owner;
        private readonly GenerationOperationType _operationType;
        private readonly int _projectId;
        private readonly int _sceneId;
        private int _disposed;

        public Lease(GpuGenerationCoordinator owner, GenerationOperationType operationType, int projectId, int sceneId)
        {
            _owner = owner;
            _operationType = operationType;
            _projectId = projectId;
            _sceneId = sceneId;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Release(_operationType, _projectId, _sceneId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
