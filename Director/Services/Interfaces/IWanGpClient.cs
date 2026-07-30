using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IWanGpClient
{
    Task<WanGpConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageModelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageToVideoModelsAsync(CancellationToken cancellationToken = default);
    Task<WanGpModelSchema?> GetModelSchemaAsync(string modelType, CancellationToken cancellationToken = default);
    Task<WanGpGenerationSubmission> SubmitImageGenerationAsync(
        WanGpImageGenerationRequest request,
        WanGpModelSchema schema,
        CancellationToken cancellationToken = default);
    Task<WanGpGenerationSubmission> SubmitVideoGenerationAsync(
        IReadOnlyDictionary<string, object?> source,
        CancellationToken cancellationToken = default);
    Task<WanGpJobSnapshot> GetJobAsync(string externalJobId, CancellationToken cancellationToken = default);
    Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken = default);
}
