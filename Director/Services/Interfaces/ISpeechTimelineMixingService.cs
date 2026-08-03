using Director.Models;

namespace Director.Services.Interfaces;

public interface ISpeechTimelineMixingService
{
    Task<SceneMediaAsset> CreateSpeechTrackAsync(int sceneSpeechPlanId, CancellationToken cancellationToken = default);
}
