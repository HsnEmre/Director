namespace Director.Services.Interfaces;

public interface IMediaOutputRecoveryLeaseCoordinator
{
    ValueTask<IAsyncDisposable> AcquireAsync(int generationJobId, CancellationToken cancellationToken = default);
}
