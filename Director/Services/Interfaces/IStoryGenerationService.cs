using Director.Dtos.StoryGeneration;

namespace Director.Services.Interfaces;

public interface IStoryGenerationService
{
    Task<StoryGenerationProgressResult> GenerateStoryAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<StoryGenerationProgressResult> GenerateAllMissingScenesAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<StoryGenerationProgressResult> GenerateNextMissingSceneAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<StoryGenerationProgressResult> GenerateUpToMissingScenesAsync(
        int filmProjectId,
        int maximumSceneCount,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
