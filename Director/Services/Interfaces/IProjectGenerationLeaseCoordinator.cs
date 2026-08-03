namespace Director.Services.Interfaces;

public interface IProjectGenerationLeaseCoordinator
{
    ValueTask<IAsyncDisposable> AcquireAsync(int filmProjectId, CancellationToken cancellationToken = default);
}
