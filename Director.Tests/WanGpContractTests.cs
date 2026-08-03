using System.Text.Json.Nodes;
using Director.Models;
using Director.Services;
using Director.WanGp;

namespace Director.Tests;

public sealed class WanGpContractTests
{
    [Fact]
    public void LtxTimingContract_UsesVideoLengthFramesForTenSeconds()
    {
        var schema = new WanGpModelSchema
        {
            ModelType = "ltx2_22B_distilled_gguf_q4_k_m",
            RawSchema = new JsonObject { ["video_length"] = new JsonObject(), ["force_fps"] = new JsonObject() },
            DefaultSettings = new JsonObject { ["force_fps"] = 24 }
        };

        var contract = new WanGpVideoTimingContractResolver().Resolve(schema, 10);

        Assert.True(contract.IsValidated);
        Assert.Equal("video_length", contract.DurationKey);
        Assert.Equal(WanGpVideoDurationUnit.Frames, contract.DurationUnit);
        Assert.Equal(240, contract.CalculatedFrameCount);
    }

    [Fact]
    public void AudioContract_UsesSchemaVoicePresetsAndDoesNotAssumeRawReferenceAudio()
    {
        var schema = new WanGpModelSchema
        {
            ModelType = "kugel_runtime_model",
            RawSchema = new JsonObject
            {
                ["prompt"] = new JsonObject(),
                ["voice_preset"] = new JsonArray("default", "warm")
            },
            DefaultSettings = new JsonObject()
        };
        var model = new WanGpModelInfo
        {
            ModelType = "kugel_runtime_model",
            DisplayName = "KugelAudio 0 Open 7B"
        };

        var contract = new WanGpAudioInputContractResolver().Resolve(model, schema);

        Assert.True(contract.IsValidated);
        Assert.Equal("prompt", contract.TextKey);
        Assert.Equal("voice_preset", contract.VoiceKey);
        Assert.Contains(contract.AvailableVoices, voice => voice.Key == "default");
        Assert.False(contract.SupportsRawReferenceAudio);
    }

    [Fact]
    public void AudioContract_DoesNotExposeMultipleFakeVoicesWithoutRuntimeVoiceKey()
    {
        var schema = new WanGpModelSchema
        {
            ModelType = "kugelaudio_0_open",
            RawSchema = new JsonObject { ["prompt"] = new JsonObject() },
            DefaultSettings = new JsonObject { ["guidance_scale"] = 3.0, ["temperature"] = 1.0 }
        };
        var model = new WanGpModelInfo
        {
            ModelType = "kugelaudio_0_open",
            DisplayName = "TTS KugelAudio 0 Open 7B"
        };

        var contract = new WanGpAudioInputContractResolver().Resolve(model, schema);

        Assert.True(contract.IsValidated);
        Assert.False(contract.SupportsVoicePreset);
        Assert.True(contract.UsesImplicitDefaultVoice);
        Assert.Single(contract.AvailableVoices);
        Assert.Equal(WanGpAudioInputContractResolver.KugelAudioDefaultVoiceKey, contract.AvailableVoices[0].Key);
        Assert.False(contract.SupportsRawReferenceAudio);
    }

    [Fact]
    public void AudioSettingsHash_IsStableForSameCharacterVoiceProfile()
    {
        var profile = VoiceProfile("warm", 1234);

        var first = AudioVoiceSettingsHasher.Compute(profile);
        var second = AudioVoiceSettingsHasher.Compute(profile);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AudioSettingsHash_ChangesWhenVoiceChanges()
    {
        var first = AudioVoiceSettingsHasher.Compute(VoiceProfile("warm", 1234));
        var second = AudioVoiceSettingsHasher.Compute(VoiceProfile("clear", 1234));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void DialogueExtractor_ReturnsNoSegmentsForEmptyDialogue()
    {
        var lines = SpeechDialogueExtractor.Extract("[]", [Character("character_1", "Ada")]);

        Assert.Empty(lines);
    }

    [Fact]
    public void DialogueExtractor_DoesNotUseNarrationText()
    {
        var lines = SpeechDialogueExtractor.Extract("[]", [Character("character_1", "Ada")]);

        Assert.Empty(lines);
    }

    [Fact]
    public void DialogueExtractor_MatchesSpeakerToStoryCharacter()
    {
        var dialogue = """
            [
              { "characterKey": "character_1", "characterName": "Ada", "text": "Buradan gitmeliyiz", "emotion": "worried" }
            ]
            """;

        var lines = SpeechDialogueExtractor.Extract(dialogue, [Character("character_1", "Ada", 42)]);

        var line = Assert.Single(lines);
        Assert.Equal(42, line.StoryCharacterId);
        Assert.Equal("character_1", line.SpeakerKey);
        Assert.Equal("Buradan gitmeliyiz", line.SourceText);
        Assert.Equal("Buradan gitmeliyiz.", line.SpokenText);
    }

    [Fact]
    public void DialogueExtractor_DoesNotAssignUnknownSpeakerRandomly()
    {
        var dialogue = """
            [
              { "characterKey": "unknown", "text": "Kimse beni tanimiyor." }
            ]
            """;

        var exception = Assert.Throws<SpeechDialogueExtractionException>(() =>
            SpeechDialogueExtractor.Extract(dialogue, [Character("character_1", "Ada")]));

        Assert.Equal(SpeechDialogueExtractionFailure.SpeakerNotFound, exception.Failure);
    }

    [Fact]
    public void DialogueExtractor_RejectsPlanWithNewDialogue()
    {
        var source = new[]
        {
            new SpeechDialogueLine { SpeakerKey = "character_1", SourceText = "Merhaba." }
        };
        var planned = new[]
        {
            new SpeechDialogueLine { SpeakerKey = "character_1", SourceText = "Yeni uydurma replik." }
        };

        Assert.Throws<InvalidOperationException>(() => SpeechDialogueExtractor.ValidateNoNewSegments(source, planned));
    }

    private static StoryCharacter Character(string key, string name, int id = 1)
    {
        return new StoryCharacter
        {
            Id = id,
            CharacterKey = key,
            Name = name,
            SortOrder = 0
        };
    }

    private static CharacterVoiceProfile VoiceProfile(string voicePresetKey, int seed)
    {
        return new CharacterVoiceProfile
        {
            ModelType = "kugelaudio_0_open",
            VoicePresetKey = voicePresetKey,
            Seed = seed,
            CfgScale = 3.0,
            DoSample = false,
            Temperature = 1.0,
            MaxNewTokens = 64,
            Language = "tr"
        };
    }
}
