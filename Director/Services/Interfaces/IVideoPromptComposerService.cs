using Director.Dtos.MediaGeneration;

namespace Director.Services.Interfaces;

public interface IVideoPromptComposerService
{
    Task<VideoPromptCompositionRequest> BuildRequestAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<VideoPromptCompositionResult> ComposeAsync(VideoPromptCompositionRequest request, CancellationToken cancellationToken = default);
}
