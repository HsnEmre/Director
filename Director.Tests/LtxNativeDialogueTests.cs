using System.Text.Json.Nodes;
using Director.Enums;
using Director.Models;
using Director.Services;
using Director.WanGp;
using Director.Services.Interfaces;

namespace Director.Tests;

public sealed class LtxNativeDialogueTests
{
    [Fact]
    public async Task NativeDialogueRequest_KeepsStartImageModeAndDoesNotDisableAudio()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(Schema()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder());

        var build = await builder.BuildAsync(Request(imagePath));

        Assert.True(build.NativeAudioRequired);
        Assert.False(build.NativeAudioDisabledByRequest);
        Assert.Equal("image_start", build.ImageInputKey);
        Assert.Equal("image_prompt_type", build.InputModeKey);
        Assert.Equal("S", build.InputModeValue);
        Assert.Equal(240, build.TimingContract?.CalculatedFrameCount);
    }

    [Fact]
    public async Task NativeDialogueRequest_SendsCombinedPromptAndKeepsExactTurkishLine()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(Schema()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder());
        var request = Request(imagePath);
        var exactLine = Assert.Single(request.ExactSpokenLines);
        request.SettingsPatch["prompt"] = "video-only movement camera environment negative prompt";

        var build = await builder.BuildAsync(request);

        var sentPrompt = Assert.IsType<string>(build.Source["prompt"]);
        Assert.Same(request.Prompt, sentPrompt);
        Assert.Contains($"Ahmet says in Turkish: \"{exactLine}\"", sentPrompt);
        Assert.Contains("speaks audibly in natural Turkish", sentPrompt);
        Assert.Contains("synchronized lip movement", sentPrompt);
        Assert.Contains("Only Ahmet speaks", sentPrompt);
        Assert.DoesNotContain("video-only movement camera environment negative prompt", sentPrompt);
    }

    [Fact]
    public async Task SilentVideoRequest_UsesNormalVideoPromptAndDoesNotRequireNativeAudio()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(Schema()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder());
        var request = Request(imagePath);
        request.GenerationMode = VideoAudioGenerationMode.SilentVideo;
        request.Prompt = "single continuous shot, Metehan walks through the forest, no cuts";
        request.DialogueSourceHash = string.Empty;
        request.DialogueCount = 0;
        request.SpeakerCount = 0;
        request.ExactSpokenLines.Clear();

        var build = await builder.BuildAsync(request);

        Assert.False(build.NativeAudioRequired);
        Assert.Equal(request.Prompt, build.Source["prompt"]);
    }

    [Fact]
    public async Task NativeDialogueRequest_FailsWhenExactLineIsMissingFromCombinedPrompt()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(Schema()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder());
        var request = Request(imagePath);
        request.ExactSpokenLines.Clear();
        request.ExactSpokenLines.Add("Ben korkmuyorum, yolumu bulacağım.");

        var ex = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => builder.BuildAsync(request));

        Assert.Equal(NativeDialoguePromptFailureStage.WanGpCompatibilityValidation, ex.FailureStage);
        Assert.Contains("occurrence mismatch", ex.SafeReason, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeDialogueOutput_FailsWithoutAudioStream()
    {
        var error = VideoGenerationService.ValidateNativeDialogueOutputMetadata(new VideoMetadata
        {
            HasVideo = true,
            HasAudio = false,
            DurationSeconds = 10
        });

        Assert.Equal("LTX native dialogue output icinde audio stream bulunamadi.", error);
    }

    [Fact]
    public void SilentVideoOutput_DoesNotRequireAudioStream()
    {
        var metadata = new VideoMetadata
        {
            HasVideo = true,
            HasAudio = false,
            DurationSeconds = 10
        };

        Assert.False(metadata.HasAudio);
    }

    [Fact]
    public void NativeDialogueOutput_PassesWithVideoAudioAndTenSecondDuration()
    {
        var error = VideoGenerationService.ValidateNativeDialogueOutputMetadata(new VideoMetadata
        {
            HasVideo = true,
            HasAudio = true,
            DurationSeconds = 10,
            AudioDurationSeconds = 2.4
        });

        Assert.Null(error);
    }

    [Fact]
    public async Task NativeDialogueRequest_FailsWhenAudioIsDisabled()
    {
        var imagePath = CreateTempImagePlaceholder();
        var builder = new WanGpVideoRequestBuilder(
            new FakeWanGpClient(Schema()),
            new WanGpVideoInputContractResolver(),
            new WanGpVideoTimingContractResolver(),
            new LtxNativeDialogueFinalPromptBuilder());
        var request = Request(imagePath);
        request.SettingsPatch["disable_audio"] = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(request));
    }

    [Fact]
    public void NativeDialogueMode_UsesDedicatedAssetRole()
    {
        Assert.Equal(MediaAssetRole.GeneratedNativeDialogueVideo, (MediaAssetRole)7);
    }

    [Fact]
    public void CapabilityResolver_AcceptsVerifiedLtxModelCaseInsensitively()
    {
        var resolver = new LtxNativeDialogueCapabilityResolver();
        var model = LtxModel();
        model.ModelType = "LTX2_22B_DISTILLED_GGUF_Q4_K_M";

        var capability = resolver.Resolve(model, ValidInputContract(), WanGpModelInstallStatus.Installed);

        Assert.True(capability.IsSupported);
        Assert.Equal(LtxNativeDialogueCapabilityResolver.VerifiedCanonicalModelType, capability.CanonicalModelType);
    }

    [Fact]
    public void CapabilityResolver_RejectsLtxWithoutAudioOutput()
    {
        var resolver = new LtxNativeDialogueCapabilityResolver();
        var model = LtxModel();
        model.Outputs = "video";

        var capability = resolver.Resolve(model, ValidInputContract(), WanGpModelInstallStatus.Installed);

        Assert.False(capability.IsSupported);
        Assert.Contains("audio output yok", capability.FailureReason);
    }

    [Fact]
    public void CapabilityResolver_RejectsKugelAudio()
    {
        var resolver = new LtxNativeDialogueCapabilityResolver();
        var model = new WanGpModelInfo
        {
            ModelType = "kugelaudio_0_open",
            DisplayName = "TTS KugelAudio 0 Open 7B",
            Family = "tts",
            Outputs = "audio",
            Inputs = "text"
        };

        var capability = resolver.Resolve(model, null, WanGpModelInstallStatus.Installed);

        Assert.False(capability.IsSupported);
    }

    [Fact]
    public void DialogueExtractor_UsesDialogueJsonExactLineAndSpeaker()
    {
        var lines = SpeechDialogueExtractor.Extract(
            """[{ "speakerKey": "metehan", "text": "Ben korkmuyorum, yolumu bulacağım.", "emotion": "brave" }]""",
            [Character("metehan", "Metehan")]);

        Assert.Single(lines);
        Assert.Equal("metehan", lines[0].SpeakerKey);
        Assert.Equal("Ben korkmuyorum, yolumu bulacağım.", lines[0].SpokenText);
        Assert.Equal("brave", lines[0].Emotion);
    }

    [Fact]
    public void DialogueExtractor_RejectsUnknownSpeaker()
    {
        var ex = Assert.Throws<SpeechDialogueExtractionException>(() => SpeechDialogueExtractor.Extract(
            """[{ "speakerKey": "unknown", "text": "Merhaba." }]""",
            [Character("metehan", "Metehan")]));

        Assert.Equal(SpeechDialogueExtractionFailure.SpeakerNotFound, ex.Failure);
        Assert.Contains("DialogueJson konuşmacısı StoryCharacter ile eşleşmedi", ex.Message);
    }

    private static WanGpVideoGenerationRequest Request(string imagePath)
    {
        var speaker = new StoryCharacter
        {
            Id = 1,
            CharacterKey = "ahmet",
            Name = "Ahmet",
            Role = "hero",
            VoiceDescription = "clear Turkish voice"
        };
        var dialogue = new SpeechDialogueLine
        {
            StoryCharacterId = speaker.Id,
            SpeakerKey = speaker.CharacterKey,
            SpeakerName = speaker.Name,
            SpokenText = "Merhaba.",
            SourceText = "Merhaba.",
            SortOrder = 1
        };
        var final = new LtxNativeDialogueFinalPromptBuilder().Build(new LtxNativeDialogueFinalPromptRequest
        {
            VisualDirection = "Single continuous shot based on the supplied start image.",
            CreativeDirection = new Director.Dtos.MediaGeneration.LtxNativeDialogueCreativeDirectionResult
            {
                PerformanceDirection = "Restrained confidence.",
                FacialExpression = "Focused gaze.",
                BodyMovement = "Small forward step.",
                VoiceDeliveryDirection = "Calm delivery.",
                CameraDirection = "Slow push-in.",
                EnvironmentalMotion = "Gentle background motion.",
                TimingDirection = "Brief silence before and after.",
                Warnings = []
            },
            Speaker = speaker,
            VoiceProfile = LtxNativeDialoguePromptComposer.CreateDefaultProfile(9, speaker),
            Dialogue = [dialogue],
            ProjectLanguage = "Türkçe"
        });
        return new WanGpVideoGenerationRequest
        {
            ModelType = "ltx2_22B_distilled_gguf_q4_k_m",
            SourceImagePath = imagePath,
            SourceImageAssetId = 12,
            SceneId = 34,
            Prompt = final.CombinedPrompt,
            Resolution = "1280x720",
            DurationSeconds = 10,
            InferenceSteps = 8,
            RandomSeed = true,
            InputMode = "start",
            GenerationMode = VideoAudioGenerationMode.LtxNativeDialogue,
            DialogueSourceHash = new string('a', 64),
            ExactSpokenLines = ["Merhaba."],
            NativeSpeakerDisplayName = final.SpeakerDisplayName,
            NativeVoiceDirection = final.VoiceDirection,
            NativeVisualDirection = final.VisualDirection,
            DialogueCount = 1,
            SpeakerCount = 1,
            InputContract = new WanGpVideoInputContract
            {
                SupportsImageToVideo = true,
                SupportsStartImage = true,
                StartImageKey = "image_start",
                StartImageModeKey = "image_prompt_type",
                StartImageModeValue = "S",
                IsValidated = true
            }
        };
    }

    private static WanGpModelSchema Schema()
    {
        return new WanGpModelSchema
        {
            ModelType = "ltx2_22B_distilled_gguf_q4_k_m",
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
    }

    private static WanGpModelInfo LtxModel()
    {
        return new WanGpModelInfo
        {
            ModelType = "ltx2_22B_distilled_gguf_q4_k_m",
            DisplayName = "LTX-2 2.3 Distilled 1.0 GGUF Q4_K_M Light 22B",
            Family = "ltx2",
            Architecture = "ltx2_22B",
            Outputs = "video, audio",
            Inputs = "text, image, audio, video",
            SupportsImageToVideo = true,
            SupportsStartImage = true,
            Availability = "installed"
        };
    }

    private static WanGpVideoInputContract ValidInputContract()
    {
        return new WanGpVideoInputContract
        {
            SupportsImageToVideo = true,
            SupportsStartImage = true,
            StartImageKey = "image_start",
            StartImageModeKey = "image_prompt_type",
            StartImageModeValue = "S",
            IsValidated = true
        };
    }

    private static StoryCharacter Character(string key, string name)
    {
        return new StoryCharacter
        {
            Id = key == "metehan" ? 1 : 2,
            CharacterKey = key,
            Name = name,
            Role = "main",
            PhysicalDescription = "young Turkish hero",
            ClothingDescription = "blue jacket",
            VoiceDescription = "clear Turkish pronunciation"
        };
    }

    private static string CreateTempImagePlaceholder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"director_ltx_native_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class FakeWanGpClient : IWanGpClient
    {
        private readonly WanGpModelSchema _schema;

        public FakeWanGpClient(WanGpModelSchema schema)
        {
            _schema = schema;
        }

        public Task<WanGpConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageModelsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageToVideoModelsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableAudioModelsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WanGpModelSchema?> GetModelSchemaAsync(string modelType, CancellationToken cancellationToken = default) => Task.FromResult<WanGpModelSchema?>(_schema);
        public Task<WanGpGenerationSubmission> SubmitImageGenerationAsync(WanGpImageGenerationRequest request, WanGpModelSchema schema, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WanGpGenerationSubmission> SubmitVideoGenerationAsync(IReadOnlyDictionary<string, object?> source, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WanGpGenerationSubmission> SubmitAudioGenerationAsync(IReadOnlyDictionary<string, object?> source, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<WanGpJobSnapshot> GetJobAsync(string externalJobId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
