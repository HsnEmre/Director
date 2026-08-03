using Director.Models;
using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IAudioGenerationService
{
    Task<AudioModelDiscoveryResult> DiscoverKugelAudioAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<SceneSpeechPlan> CreateBasicSpeechPlanAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset> GenerateSpeechSegmentAsync(int speechSegmentId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset> CreateSpeechTrackForSceneAsync(int sceneId, CancellationToken cancellationToken = default);
    Task<SceneMediaAsset> CreateFinalDialogueVideoForSceneAsync(int sceneId, CancellationToken cancellationToken = default);
}

public sealed class AudioModelDiscoveryResult
{
    public WanGpModelInfo? Model { get; set; }
    public WanGpLocalModelInventoryItem? Inventory { get; set; }
    public WanGpModelSchema? Schema { get; set; }
    public WanGpAudioInputContract? Contract { get; set; }
    public bool IsInstalled => Inventory?.Status == WanGpModelInstallStatus.Installed || Model?.IsAvailable == true;
    public bool IsSelectable => Model is not null && IsInstalled && Contract?.IsValidated == true;
    public string Message { get; set; } = string.Empty;
}
