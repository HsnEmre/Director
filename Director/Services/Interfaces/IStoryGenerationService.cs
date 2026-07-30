using Director.Dtos.StoryGeneration;

namespace Director.Services.Interfaces;

public interface IStoryGenerationService
{
    Task<StoryGenerationProgressResult> GenerateStoryAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
