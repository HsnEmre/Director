using System.Text.Json;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Director.Tests;

public sealed class NativeDialoguePromptCompositionTests
{
    [Fact]
    public async Task ValidStructuredResponse_PassesCentralParserAndValidation()
    {
        var fixture = Fixture(ValidRaw());

        var detailed = await fixture.ComposeAsync(allowRepair: true);

        Assert.False(string.IsNullOrWhiteSpace(detailed.Value.PerformanceDirection));
        Assert.Equal("Passed", fixture.Result.ValidationResult);
        Assert.Equal("JsonObject", fixture.Result.RawResponseShape);
        Assert.Equal(1, fixture.Client.CallCount);
        Assert.False(fixture.Result.RepairUsed);
    }

    [Fact]
    public async Task CodeFenceResponse_PassesCentralParser()
    {
        var fixture = Fixture($"```json\n{ValidRaw()}\n```");

        await fixture.ComposeAsync(allowRepair: false);

        Assert.Equal("CodeFence", fixture.Result.RawResponseShape);
    }

    [Fact]
    public async Task ExplanationBeforeJson_PassesCentralParser()
    {
        var fixture = Fixture($"Here is the result:\n{ValidRaw()}\nCompleted.");

        await fixture.ComposeAsync(allowRepair: false);

        Assert.Equal("ExplanationWithJson", fixture.Result.RawResponseShape);
    }

    [Fact]
    public async Task InvalidInitialValidRepair_UsesExactlyTwo30BCalls()
    {
        var fixture = Fixture("{}", ValidRaw());

        await fixture.ComposeAsync(allowRepair: true);

        Assert.Equal(2, fixture.Client.CallCount);
        Assert.True(fixture.Result.RepairUsed);
        Assert.All(fixture.Client.Models, model => Assert.Equal(OllamaOptions.DefaultTextModel, model));
    }

    [Fact]
    public async Task InvalidInitialInvalidRepair_ReturnsTypedExceptionAndNoThirdCall()
    {
        var fixture = Fixture("{}", "{}");

        var exception = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: true));

        Assert.Equal(NativeDialoguePromptFailureStage.ResponseValidation, exception.FailureStage);
        Assert.Equal(2, fixture.Client.CallCount);
        Assert.Equal(0, fixture.WanGpSubmitCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public async Task EmptyOrWhitespaceResponse_IsTypedParsingFailure(string raw)
    {
        var fixture = Fixture(raw);

        var exception = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: false));

        Assert.Equal(NativeDialoguePromptFailureStage.OllamaResponseParsing, exception.FailureStage);
        Assert.Equal(1, fixture.Client.CallCount);
    }

    [Fact]
    public void EmptyDialogueJson_IsValidVisualOnlyInput()
    {
        var lines = SpeechDialogueExtractor.Extract("[]", [Character()]);

        Assert.Empty(lines);
    }

    [Fact]
    public void InvalidDialogueJson_ReportsParsingFailure()
    {
        var exception = Assert.Throws<SpeechDialogueExtractionException>(() =>
            SpeechDialogueExtractor.Extract("[{", [Character()]));

        Assert.Equal(SpeechDialogueExtractionFailure.InvalidJson, exception.Failure);
    }

    [Fact]
    public void UnknownSpeaker_ReportsSpeakerResolutionFailure()
    {
        var exception = Assert.Throws<SpeechDialogueExtractionException>(() =>
            SpeechDialogueExtractor.Extract("""[{"speakerKey":"unknown","text":"Merhaba."}]""", [Character()]));

        Assert.Equal(SpeechDialogueExtractionFailure.SpeakerNotFound, exception.Failure);
        Assert.Equal("unknown", exception.SpeakerKey);
    }

    [Fact]
    public void SpeakerKeyCasingDifference_ResolvesToCanonicalCharacterKey()
    {
        var lines = SpeechDialogueExtractor.Extract("""[{"speakerKey":"METEHAN","text":"Merhaba."}]""", [Character()]);

        Assert.Equal("metehan", Assert.Single(lines).SpeakerKey);
    }

    [Fact]
    public void CaseInsensitiveDuplicateKeys_ReportAmbiguity()
    {
        var duplicate = Character();
        duplicate.Id = 2;
        duplicate.CharacterKey = "METEHAN";
        var exception = Assert.Throws<SpeechDialogueExtractionException>(() =>
            SpeechDialogueExtractor.Extract("""[{"speakerKey":"metehan","text":"Merhaba."}]""", [Character(), duplicate]));

        Assert.Equal(SpeechDialogueExtractionFailure.AmbiguousSpeaker, exception.Failure);
    }

    [Fact]
    public void ExistingValidVoiceProfile_HasNoMissingFields()
    {
        var profile = LtxNativeDialoguePromptComposer.CreateDefaultProfile(9, Character());
        profile.Id = 7;

        Assert.Empty(LtxNativeDialoguePromptComposer.ValidateVoiceProfile(profile));
    }

    [Fact]
    public void MissingVoiceProfile_GeneratesValidatedInMemoryProfile()
    {
        var generated = LtxNativeDialoguePromptComposer.CreateDefaultProfile(9, Character());

        Assert.Equal(0, generated.Id);
        Assert.Empty(LtxNativeDialoguePromptComposer.ValidateVoiceProfile(generated));
        Assert.False(string.IsNullOrWhiteSpace(generated.SettingsHash));
    }

    [Fact]
    public void InvalidGeneratedVoiceProfile_IsRejectedBeforeUse()
    {
        var generated = LtxNativeDialoguePromptComposer.CreateDefaultProfile(9, Character());
        generated.VoiceDescription = string.Empty;

        Assert.Contains(nameof(LtxNativeVoiceProfile.VoiceDescription), LtxNativeDialoguePromptComposer.ValidateVoiceProfile(generated));
    }

    [Fact]
    public void ExistingVoiceProfileWithRequiredFieldMissing_IsRejected()
    {
        var profile = LtxNativeDialoguePromptComposer.CreateDefaultProfile(9, Character());
        profile.Id = 7;
        profile.AccentDescription = " ";

        Assert.Contains(nameof(LtxNativeVoiceProfile.AccentDescription), LtxNativeDialoguePromptComposer.ValidateVoiceProfile(profile));
    }

    [Fact]
    public async Task SemanticFailure_CreatesBoundedDiagnosticWithCorrectStage()
    {
        var root = Path.Combine(Path.GetTempPath(), "DirectorNativeDialogueTests", Guid.NewGuid().ToString("N"));
        var fixture = FixtureWithDiagnostic(new OllamaFailureDiagnosticWriter(root), "{}");

        var exception = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: false));

        Assert.Equal(NativeDialoguePromptFailureStage.ResponseValidation, exception.FailureStage);
        Assert.True(File.Exists(exception.DiagnosticPath));
        var payload = await File.ReadAllTextAsync(exception.DiagnosticPath);
        Assert.Contains("ResponseValidation", payload);
        Assert.Contains(LtxNativeDialoguePromptComposer.OperationName, payload);
        Assert.DoesNotContain("connectionString", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticWriterFailure_DoesNotMaskTypedCompositionFailure()
    {
        var fixture = FixtureWithDiagnostic(new ThrowingDiagnosticWriter(), "{}");

        var exception = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: false));

        Assert.Equal(NativeDialoguePromptFailureStage.ResponseValidation, exception.FailureStage);
        Assert.Equal(string.Empty, exception.DiagnosticPath);
    }

    [Fact]
    public async Task NativeDialogueFailure_PreventsWanGpAssetAndJobWork()
    {
        var fixture = Fixture("{}");

        await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: false));

        Assert.Equal(0, fixture.WanGpSubmitCount);
        Assert.Equal(0, fixture.VideoAssetInsertCount);
        Assert.Equal(0, fixture.GenerationJobCreateCount);
        Assert.Equal(0, fixture.StaleActiveJobCount);
    }

    [Fact]
    public async Task Failure_ReleasesGpuLease()
    {
        var fixture = Fixture("{}");

        await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: false));

        Assert.False(fixture.Gpu.IsBusy);
        Assert.Equal(fixture.Gpu.AcquireCount, fixture.Gpu.ReleaseCount);
    }

    [Fact]
    public async Task CompositionNeverCallsFourBModel()
    {
        var fixture = Fixture(ValidRaw());

        await fixture.ComposeAsync(allowRepair: true);

        Assert.Equal(0, fixture.Client.Models.Count(model => model.Contains("4b", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DoneStopWithSemanticFailure_ReportsResponseValidation()
    {
        var fixture = Fixture("{}");

        var exception = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() => fixture.ComposeAsync(allowRepair: false));

        Assert.Equal(NativeDialoguePromptFailureStage.ResponseValidation, exception.FailureStage);
        Assert.True(fixture.Client.LastMetadata!.Done);
        Assert.Equal("stop", fixture.Client.LastMetadata.DoneReason);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("too-large")]
    [InlineData("truncated")]
    public async Task TransportAndSizeFailures_DoNotTriggerRepair(string failureKind)
    {
        var metadata = new OllamaResponseMetadata { Model = OllamaOptions.DefaultTextModel };
        OllamaResponseException failure = failureKind switch
        {
            "http" => new OllamaHttpResponseException("HTTP 500", "error", metadata),
            "too-large" => new OllamaResponseTooLargeException("too large", "{partial", metadata),
            _ => new OllamaResponseTruncatedException("truncated", "{partial", metadata)
        };
        var client = new ExceptionOllamaClient(failure);
        var gpu = new RecordingGpuCoordinator();
        var composer = Composer(client, gpu);

        var exception = await Assert.ThrowsAsync<NativeDialoguePromptCompositionException>(() =>
            composer.ComposeForTestingAsync(Scene(), [Character()], [Dialogue()], CreateTempImage(), new(), allowRepair: true));

        Assert.Equal(NativeDialoguePromptFailureStage.OllamaTransport, exception.FailureStage);
        Assert.Equal(1, client.CallCount);
        Assert.False(gpu.IsBusy);
    }

    [Fact]
    public async Task Cancellation_DoesNotTriggerRepair()
    {
        var client = new ExceptionOllamaClient(new OperationCanceledException("cancelled"));
        var gpu = new RecordingGpuCoordinator();
        var composer = Composer(client, gpu);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            composer.ComposeForTestingAsync(Scene(), [Character()], [Dialogue()], CreateTempImage(), new(), allowRepair: true));

        Assert.Equal(1, client.CallCount);
        Assert.False(gpu.IsBusy);
    }

    [Fact]
    public void ModelCombinedPromptWithoutBoilerplate_IsIgnoredByCreativeValidation()
    {
        var response = ValidResponse();
        response.AdditionalFields = new Dictionary<string, JsonElement>
        {
            ["combinedPrompt"] = JsonDocument.Parse("\"wrong model dialogue\"").RootElement.Clone()
        };

        var errors = LtxNativeDialoguePromptComposer.ValidateCreativeDirectionResult(response, [Dialogue()], [Character()]);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task WrongModelCombinedPrompt_DoesNotLeakIntoDeterministicFinalPromptOrTriggerRepair()
    {
        var response = ValidResponse();
        response.AdditionalFields = new Dictionary<string, JsonElement>
        {
            ["combinedPrompt"] = JsonDocument.Parse("\"Teoman says in Turkish: Yanlış replik\"").RootElement.Clone()
        };
        var fixture = Fixture(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var detailed = await fixture.ComposeAsync(allowRepair: true);
        var final = fixture.BuildFinal(detailed.Value);

        Assert.Equal(1, fixture.Client.CallCount);
        Assert.False(fixture.Result.RepairUsed);
        Assert.DoesNotContain("Yanlış replik", final.CombinedPrompt, StringComparison.Ordinal);
        Assert.Contains("Metehan says in Turkish: \"Merhaba.\"", final.CombinedPrompt, StringComparison.Ordinal);
        Assert.Contains("Only Metehan speaks", final.CombinedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewQuotedDialogueInCreativeField_UsesOneRepairThenAssembles()
    {
        var invalid = ValidResponse();
        invalid.PerformanceDirection = "He says \"Yeni replik.\"";
        var fixture = Fixture(
            JsonSerializer.Serialize(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            ValidRaw());

        var detailed = await fixture.ComposeAsync(allowRepair: true);
        var final = fixture.BuildFinal(detailed.Value);

        Assert.Equal(2, fixture.Client.CallCount);
        Assert.True(fixture.Result.RepairUsed);
        Assert.DoesNotContain("Yeni replik", final.CombinedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulComposition_ReleasesGpuLease()
    {
        var fixture = Fixture(ValidRaw());

        await fixture.ComposeAsync(allowRepair: false);

        Assert.False(fixture.Gpu.IsBusy);
        Assert.Equal(fixture.Gpu.AcquireCount, fixture.Gpu.ReleaseCount);
    }

    [Fact]
    public void MultipleSpeakers_AreRejectedBeforeAnyModelCall()
    {
        var fixture = Fixture(ValidRaw());
        var other = Dialogue();
        other.StoryCharacterId = 2;
        other.SpeakerKey = "teoman";
        other.SpeakerName = "Teoman";

        var exception = Assert.Throws<NativeDialoguePromptCompositionException>(() =>
            LtxNativeDialoguePromptComposer.ValidateDialogueDomainForTesting(Scene(), [Dialogue(), other]));

        Assert.Equal(NativeDialoguePromptFailureStage.SceneInputValidation, exception.FailureStage);
        Assert.Equal(0, fixture.Client.CallCount);
    }

    [Fact]
    public void EmptySpeakerDisplayName_IsRejectedBeforeAnyModelCall()
    {
        var fixture = Fixture(ValidRaw());
        var dialogue = Dialogue();
        dialogue.SpeakerName = " ";

        var exception = Assert.Throws<NativeDialoguePromptCompositionException>(() =>
            LtxNativeDialoguePromptComposer.ValidateDialogueDomainForTesting(Scene(), [dialogue]));

        Assert.Equal(NativeDialoguePromptFailureStage.SpeakerResolution, exception.FailureStage);
        Assert.Equal(0, fixture.Client.CallCount);
    }

    private static CompositionFixture Fixture(params string[] rawResponses) =>
        FixtureWithDiagnostic(new RecordingDiagnosticWriter(), rawResponses);

    private static CompositionFixture FixtureWithDiagnostic(IOllamaFailureDiagnosticWriter diagnosticWriter, params string[] rawResponses)
    {
        var client = new RawResponseOllamaClient(rawResponses);
        var gpu = new RecordingGpuCoordinator();
        var composer = new LtxNativeDialoguePromptComposer(
            null!,
            client,
            gpu,
            diagnosticWriter,
            new LtxNativeDialogueFinalPromptBuilder(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            NullLogger<LtxNativeDialoguePromptComposer>.Instance);
        return new CompositionFixture(composer, client, gpu);
    }

    private static LtxNativeDialoguePromptComposer Composer(IOllamaClient client, IGpuGenerationCoordinator gpu) =>
        new(null!, client, gpu, new RecordingDiagnosticWriter(), new LtxNativeDialogueFinalPromptBuilder(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()),
            NullLogger<LtxNativeDialoguePromptComposer>.Instance);

    private static string ValidRaw() => JsonSerializer.Serialize(ValidResponse(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static LtxNativeDialogueCreativeDirectionResult ValidResponse()
    {
        return new LtxNativeDialogueCreativeDirectionResult
        {
            PerformanceDirection = "A restrained, confident performance.",
            FacialExpression = "A steady focused gaze.",
            BodyMovement = "A subtle forward step.",
            VoiceDeliveryDirection = "Calm and measured delivery.",
            CameraDirection = "A slow stable push-in.",
            EnvironmentalMotion = "Leaves move gently in the background.",
            TimingDirection = "Brief silent motion before and after speech.",
            Warnings = []
        };
    }

    private static FilmScene Scene() => new()
    {
        Id = 36,
        FilmProjectId = 9,
        SceneNumber = 17,
        DurationSeconds = 10,
        Title = "Test",
        StoryBeat = "Metehan speaks.",
        SceneDescription = "Forest scene.",
        VideoPrompt = "One continuous forest shot.",
        LocationDescription = "Forest",
        TimeOfDay = "Day",
        DialogueJson = """[{"speakerKey":"metehan","text":"Merhaba."}]"""
    };

    private static StoryCharacter Character() => new()
    {
        Id = 1,
        CharacterKey = "metehan",
        Name = "Metehan",
        Role = "hero",
        PhysicalDescription = "young Turkish hero",
        ClothingDescription = "blue jacket",
        VoiceDescription = "clear Turkish voice"
    };

    private static SpeechDialogueLine Dialogue() => new()
    {
        SpeakerKey = "metehan",
        SpeakerName = "Metehan",
        SourceText = "Merhaba.",
        SpokenText = "Merhaba.",
        Emotion = "calm",
        SortOrder = 1,
        StoryCharacterId = 1
    };

    private static string CreateTempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"director_native_prompt_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    private sealed class CompositionFixture(
        LtxNativeDialoguePromptComposer composer,
        RawResponseOllamaClient client,
        RecordingGpuCoordinator gpu)
    {
        public LtxNativeDialoguePromptResult Result { get; } = new();
        public RawResponseOllamaClient Client => client;
        public RecordingGpuCoordinator Gpu => gpu;
        public int WanGpSubmitCount { get; private set; }
        public int VideoAssetInsertCount { get; private set; }
        public int GenerationJobCreateCount { get; private set; }
        public int StaleActiveJobCount { get; private set; }

        public Task<OllamaStructuredResult<LtxNativeDialogueCreativeDirectionResult>> ComposeAsync(bool allowRepair) =>
            composer.ComposeForTestingAsync(Scene(), [Character()], [Dialogue()], CreateTempImage(), Result, allowRepair);

        public LtxNativeDialogueFinalPrompt BuildFinal(LtxNativeDialogueCreativeDirectionResult creative) =>
            composer.BuildFinalPromptForTesting(
                Scene(), [Character()], [Dialogue()],
                LtxNativeDialoguePromptComposer.CreateDefaultProfile(9, Character()), creative);
    }

    private sealed class RawResponseOllamaClient(IEnumerable<string> rawResponses) : IOllamaClient
    {
        private readonly Queue<string> _responses = new(rawResponses);
        private readonly OllamaStructuredJsonParser _parser = new();
        public int CallCount { get; private set; }
        public List<string> Models { get; } = [];
        public OllamaResponseMetadata? LastMetadata { get; private set; }

        public Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public async Task<TResponse> ChatStructuredAsync<TResponse>(IReadOnlyList<OllamaChatMessage> messages, object jsonSchema, string? modelOverride = null, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default, IProgress<OllamaStreamProgress>? streamProgress = null, OllamaGenerationSettings? generationSettings = null) =>
            (await ChatStructuredDetailedAsync<TResponse>(messages, jsonSchema, modelOverride, requestTimeout, cancellationToken, streamProgress, generationSettings)).Value;

        public Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(IReadOnlyList<OllamaChatMessage> messages, object jsonSchema, string? modelOverride = null, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default, IProgress<OllamaStreamProgress>? streamProgress = null, OllamaGenerationSettings? generationSettings = null)
        {
            CallCount++;
            Models.Add(modelOverride ?? string.Empty);
            var raw = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            LastMetadata = new OllamaResponseMetadata
            {
                Model = modelOverride ?? string.Empty,
                OperationName = generationSettings?.OperationName ?? string.Empty,
                FilmProjectId = generationSettings?.FilmProjectId,
                SceneNumber = generationSettings?.SceneNumber,
                Done = true,
                StreamCompleted = true,
                DoneReason = "stop",
                PromptTokenCount = 100,
                ResponseTokenCount = 50,
                ResponseCharacterCount = raw.Length
            };
            return Task.FromResult(_parser.Parse<TResponse>(raw, LastMetadata));
        }
    }

    private sealed class RecordingGpuCoordinator : IGpuGenerationCoordinator
    {
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }
        public bool IsBusy { get; private set; }
        public Task<IAsyncDisposable> AcquireAsync(GenerationOperationType operationType, int projectId, int sceneId, CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            IsBusy = true;
            return Task.FromResult<IAsyncDisposable>(new CallbackLease(() =>
            {
                ReleaseCount++;
                IsBusy = false;
            }));
        }
    }

    private sealed class ExceptionOllamaClient(Exception exception) : IOllamaClient
    {
        public int CallCount { get; private set; }
        public Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TResponse> ChatStructuredAsync<TResponse>(IReadOnlyList<OllamaChatMessage> messages, object jsonSchema,
            string? modelOverride = null, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default,
            IProgress<OllamaStreamProgress>? streamProgress = null, OllamaGenerationSettings? generationSettings = null) =>
            throw new NotSupportedException();
        public Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(
            IReadOnlyList<OllamaChatMessage> messages, object jsonSchema, string? modelOverride = null,
            TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default,
            IProgress<OllamaStreamProgress>? streamProgress = null, OllamaGenerationSettings? generationSettings = null)
        {
            CallCount++;
            return Task.FromException<OllamaStructuredResult<TResponse>>(exception);
        }
    }

    private sealed class CallbackLease(Action callback) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) callback();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingDiagnosticWriter : IOllamaFailureDiagnosticWriter
    {
        public Task<string> WriteAsync(OllamaFailureContext context, string attemptType, OllamaResponseException exception, CancellationToken cancellationToken = default) =>
            Task.FromResult($"C:\\diagnostics\\{attemptType}.json");
    }

    private sealed class ThrowingDiagnosticWriter : IOllamaFailureDiagnosticWriter
    {
        public Task<string> WriteAsync(OllamaFailureContext context, string attemptType, OllamaResponseException exception, CancellationToken cancellationToken = default) =>
            throw new IOException("diagnostic unavailable");
    }
}
