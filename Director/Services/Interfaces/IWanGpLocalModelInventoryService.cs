using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpLocalModelInventoryService
{
    Task<IReadOnlyDictionary<string, WanGpLocalModelInventoryItem>> GetInventoryAsync(
        IReadOnlyList<WanGpModelInfo> models,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}
