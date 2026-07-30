using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpRuntimeCoordinator
{
    WanGpRuntimeStatus LastStatus { get; }
    Task<WanGpRuntimeStatus> EnsureReadyAsync(CancellationToken cancellationToken = default);
    Task<WanGpRuntimeStatus> RefreshStatusAsync(CancellationToken cancellationToken = default);
    Task StopOwnedProcessAsync(CancellationToken cancellationToken = default);
}
