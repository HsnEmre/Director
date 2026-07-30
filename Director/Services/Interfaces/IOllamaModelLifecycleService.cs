namespace Director.Services.Interfaces;

public interface IOllamaModelLifecycleService
{
    Task<IReadOnlyList<string>> GetRunningModelsAsync(CancellationToken cancellationToken = default);
    Task UnloadModelAsync(string modelName, CancellationToken cancellationToken = default);
    Task<bool> WaitUntilUnloadedAsync(string modelName, TimeSpan timeout, CancellationToken cancellationToken = default);
}
