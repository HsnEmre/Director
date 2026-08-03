using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpVideoInputContractResolver
{
    Task<WanGpVideoInputContract> ResolveAsync(
        WanGpModelInfo model,
        WanGpModelSchema? schema,
        IReadOnlyDictionary<string, object?> defaults,
        CancellationToken cancellationToken = default);
}
