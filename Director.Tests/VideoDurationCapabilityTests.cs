using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Dtos.Autonomous;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Models;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;

namespace Director.Tests;

public sealed class VideoDurationCapabilityTests
{
    [Fact]
    public async Task LtxSupportedDuration_PassesThroughRequestFactory()
    {
        var imagePath = CreateTempImagePlaceholder();
        var factory = Factory(new FakeWanGpClient(LtxSchema(), LtxModel()));

        var request = await factory.CreateAsync(Input(durationSeconds: 10, imagePath));

        Assert.Equal(10, request.DurationSeconds);
        Assert.Equal("ltx2_22B_distilled_gguf_q4_k_m", request.ModelType);
    }

    [Fact]
    public async Task LtxUnsupportedSixtySeconds_FailsBeforeWanGpSubmit()
    {
        var imagePath = CreateTempImagePlaceholder();
        var client = new FakeWanGpClient(LtxSchema(), LtxModel());
        var factory = Factory(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            factory.CreateAsync(Input(durationSeconds: 60, imagePath)));

        Assert.Contains("60 saniyelik", exception.Message);
        Assert.Contains("10 saniye", exception.Message);
        Assert.Equal(0, client.SubmitVideoCallCount);
    }

    [Fact]
    public void ManualAndAutonomousUseSameCapabilityValidation()
    {
        var service = new VideoModelCapabilityService();

        var manual = service.ValidateDuration(VideoModelCapabilityService.VerifiedLtxModelType, 60);
        var autonomous = service.ValidateSnapshot(VideoModelCapabilityService.VerifiedLtxModelType, 60, 1);

        Assert.False(manual.IsValid);
        Assert.False(autonomous.IsValid);
        Assert.Equal(manual.ErrorMessage, autonomous.ErrorMessage);
    }

    [Fact]
    public void SupportedTenSecondSmokeDuration_CalculatesSingleScene()
    {
        var clipCount = FilmDurationPlanner.CalculateClipCountForTargetSeconds(10, 10);

        Assert.Equal(1, clipCount);
        Assert.Equal(10, FilmDurationPlanner.CalculateOutputDurationSeconds(clipCount, 10));
    }

    [Fact]
    public void FailedRunSnapshot_RemainsImmutableWhenProjectDurationChanges()
    {
        var snapshot = new AutonomousGenerationConfigurationSnapshot
        {
            FilmProjectId = 10,
            ClipDurationSeconds = 60,
            CalculatedClipCount = 1,
            VideoModelType = VideoModelCapabilityService.VerifiedLtxModelType,
            Resolution = "1280x720",
            GenerateAudio = false,
            PreferLtxNativeDialogue = true
        };
        var snapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var project = new FilmProject
        {
            Id = 10,
            ClipDurationSeconds = 10,
            CalculatedClipCount = 1
        };

        var reloaded = JsonSerializer.Deserialize<AutonomousGenerationConfigurationSnapshot>(
            snapshotJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(reloaded);
        Assert.Equal(60, reloaded!.ClipDurationSeconds);
        Assert.Equal(1, reloaded.CalculatedClipCount);
        Assert.Equal(10, project.ClipDurationSeconds);
    }

    [Fact]
    public void RetryCompatibility_InvalidSnapshotFailsBeforeRetryStarts()
    {
        var service = new VideoModelCapabilityService();

        var validation = service.ValidateSnapshot(VideoModelCapabilityService.VerifiedLtxModelType, 60, 1);

        Assert.False(validation.IsValid);
        Assert.Contains("desteklemiyor", validation.ErrorMessage);
    }

    [Fact]
    public async Task WanGpBuilder_ConvertsSupportedTenSecondsToVideoLengthFrames()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(LtxSchema(), LtxModel()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder(),
            new VideoModelCapabilityService());

        var build = await builder.BuildAsync(VideoRequest(durationSeconds: 10, imagePath));

        Assert.Equal("video_length", build.TimingContract?.DurationKey);
        Assert.Equal(WanGpVideoDurationUnit.Frames, build.TimingContract?.DurationUnit);
        Assert.Equal(240, build.TimingContract?.CalculatedFrameCount);
        Assert.Equal(240, build.Source["video_length"]);
    }

    [Fact]
    public async Task WanGpBuilder_RejectsSixtySecondsBeforeFrameConversion()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(LtxSchema(), LtxModel()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder(),
            new VideoModelCapabilityService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(VideoRequest(durationSeconds: 60, imagePath)));

        Assert.Contains("60 saniyelik", exception.Message);
    }

    private static VideoGenerationRequestFactory Factory(IWanGpClient client) =>
        new(
            client,
            new WanGpVideoInputContractResolver(),
            new NoopNativeDialoguePromptComposer(),
            new LtxNativeDialogueCapabilityResolver(),
            new VideoModelCapabilityService());

    private static VideoGenerationRequestFactoryInput Input(int durationSeconds, string imagePath) => new()
    {
        FilmProjectId = 10,
        Scene = Scene(durationSeconds),
        SourceImageAsset = ImageAsset(imagePath),
        ModelType = VideoModelCapabilityService.VerifiedLtxModelType,
        Resolution = "1280x720",
        InferenceSteps = 12,
        RandomSeed = true,
        PreferNativeDialogue = true
    };

    private static WanGpVideoGenerationRequest VideoRequest(int durationSeconds, string imagePath) => new()
    {
        FilmProjectId = 10,
        SceneId = 34,
        SceneNumber = 1,
        SourceImageAssetId = 12,
        SourceImagePath = imagePath,
        ModelType = VideoModelCapabilityService.VerifiedLtxModelType,
        Prompt = "single continuous shot",
        NegativePrompt = "text, watermark",
        Resolution = "1280x720",
        DurationSeconds = durationSeconds,
        InferenceSteps = 12,
        RandomSeed = true,
        GenerationMode = VideoAudioGenerationMode.SilentVideo
    };

    private static FilmScene Scene(int durationSeconds) => new()
    {
        Id = 34,
        FilmProjectId = 10,
        SceneNumber = 1,
        DurationSeconds = durationSeconds,
        VideoPrompt = "single continuous shot",
        VideoNegativePrompt = "text, watermark",
        DialogueJson = "[]"
    };

    private static SceneMediaAsset ImageAsset(string imagePath) => new()
    {
        Id = 12,
        FilmProjectId = 10,
        SceneId = 34,
        FilePath = imagePath,
        MediaType = MediaType.Image,
        Role = MediaAssetRole.ReferenceImage,
        IsSelected = true
    };

    private static WanGpModelInfo LtxModel() => new()
    {
        ModelType = VideoModelCapabilityService.VerifiedLtxModelType,
        DisplayName = "LTX-2 2.3 Distilled 1.0 GGUF Q4_K_M Light 22B",
        Family = "ltx2",
        Outputs = "video, audio",
        Inputs = "text, image, audio, video",
        SupportsImageToVideo = true,
        SupportsStartImage = true,
        Availability = "installed",
        InstallStatus = WanGpModelInstallStatus.Installed
    };

    private static WanGpModelSchema LtxSchema() => new()
    {
        ModelType = VideoModelCapabilityService.VerifiedLtxModelType,
        RawSchema = new JsonObject
        {
            ["prompt"] = new JsonObject(),
            ["image_start"] = new JsonObject(),
            ["image_prompt_type"] = new JsonObject(),
            ["video_length"] = new JsonObject(),
            ["force_fps"] = new JsonObject(),
            ["disable_audio"] = new JsonObject()
        },
        DefaultSettings = new JsonObject
        {
            ["force_fps"] = 24,
            ["image_prompt_type"] = "S",
            ["disable_audio"] = false
        }
    };

    private static string CreateTempImagePlaceholder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"director_duration_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class NoopNativeDialoguePromptComposer : ILtxNativeDialoguePromptComposer
    {
        public Task<LtxNativeDialoguePromptResult> BuildAsync(
            int sceneId,
            int sourceImageAssetId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<LtxNativeDialoguePromptResult> BuildReadOnlyAsync(
            int sceneId,
            int referenceImageAssetId,
            bool allowRepair = false,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWanGpClient(WanGpModelSchema schema, WanGpModelInfo model) : IWanGpClient
    {
        public int SubmitVideoCallCount { get; private set; }

        public Task<WanGpConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageToVideoModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WanGpModelInfo>>([model]);
        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableAudioModelsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WanGpModelSchema?> GetModelSchemaAsync(string modelType, CancellationToken cancellationToken = default) => Task.FromResult<WanGpModelSchema?>(schema);
        public Task<WanGpGenerationSubmission> SubmitImageGenerationAsync(WanGpImageGenerationRequest request, WanGpModelSchema schema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WanGpGenerationSubmission> SubmitVideoGenerationAsync(IReadOnlyDictionary<string, object?> source, CancellationToken cancellationToken = default)
        {
            SubmitVideoCallCount++;
            throw new NotSupportedException();
        }

        public Task<WanGpGenerationSubmission> SubmitAudioGenerationAsync(IReadOnlyDictionary<string, object?> source, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WanGpJobSnapshot> GetJobAsync(string externalJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
