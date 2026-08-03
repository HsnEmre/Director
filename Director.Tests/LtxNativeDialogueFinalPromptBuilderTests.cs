using System.Globalization;
using System.Text.Json;
using Director.Dtos.MediaGeneration;
using Director.Models;
using Director.Services;
using Director.Services.Interfaces;

namespace Director.Tests;

public sealed class LtxNativeDialogueFinalPromptBuilderTests
{
    private readonly LtxNativeDialogueFinalPromptBuilder _builder = new();

    [Fact]
    public void SingleSpeaker_CanonicalPromptMatchesSnapshot()
    {
        var result = Build("Merhaba.");

        Assert.Equal("Metehan says in Turkish: \"Merhaba.\"", Assert.Single(result.NamedSpeakerLines));
        Assert.Equal("Only Metehan speaks", result.OnlySpeakerLine);
        Assert.Equal(ExpectedSnapshot(), result.CombinedPrompt);
    }

    [Theory]
    [InlineData("Türkçe karakterler: ğüşöçıİ.")]
    [InlineData("Bana \"dur\" dedi.")]
    [InlineData("C:\\yol\\dosya\nİkinci satır.")]
    public void AuthoritativeDialogue_IsEscapedAndPreservedDeterministically(string exactDialogue)
    {
        var result = Build(exactDialogue);
        var canonical = $"Metehan says in Turkish: {LtxNativeDialogueFinalPromptBuilder.Quote(exactDialogue)}";

        Assert.Equal(canonical, Assert.Single(result.NamedSpeakerLines));
        Assert.Contains(canonical, result.CombinedPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u011f", result.CombinedPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelCombinedPromptAndWrongDialogue_AreIgnored()
    {
        var creative = Creative();
        creative.AdditionalFields = new Dictionary<string, JsonElement>
        {
            ["combinedPrompt"] = JsonDocument.Parse("\"Ayşe says in Turkish: Yanlış metin\"").RootElement.Clone()
        };

        var result = Build("Doğru metin.", creative);

        Assert.DoesNotContain("Yanlış metin", result.CombinedPrompt, StringComparison.Ordinal);
        Assert.Contains("Metehan says in Turkish: \"Doğru metin.\"", result.CombinedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CharacterDisplayName_EmptyIsRejected()
    {
        var speaker = Speaker();
        speaker.Name = " ";

        var exception = Assert.Throws<LtxNativeDialogueFinalPromptValidationException>(() => Build("Merhaba.", speaker: speaker));

        Assert.Contains("StoryCharacter.Name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameSpeakerMultipleEntries_PreserveOrder()
    {
        var speaker = Speaker();
        var request = Request("Birinci.", speaker);
        request.Dialogue =
        [
            Dialogue("İkinci.", speaker, 2),
            Dialogue("Birinci.", speaker, 1)
        ];

        var result = _builder.Build(request);

        Assert.Equal(["Metehan says in Turkish: \"Birinci.\"", "Metehan says in Turkish: \"İkinci.\""], result.NamedSpeakerLines);
    }

    [Fact]
    public void MultipleSpeakers_AreRejectedWithoutSelectingFirst()
    {
        var speaker = Speaker();
        var other = Speaker();
        other.Id = 2;
        other.Name = "Teoman";
        var request = Request("Birinci.", speaker);
        request.Dialogue = [Dialogue("Birinci.", speaker, 1), Dialogue("İkinci.", other, 2)];

        var exception = Assert.Throws<LtxNativeDialogueFinalPromptValidationException>(() => _builder.Build(request));

        Assert.Contains("exactly one resolved speaker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreativeDirectionWithNewQuotedDialogue_IsRejected()
    {
        var creative = Creative();
        creative.PerformanceDirection = "He says \"Yeni bir replik.\"";

        var errors = LtxNativeDialoguePromptComposer.ValidateCreativeDirectionResult(
            creative, [Dialogue("Merhaba.", Speaker(), 1)], [Speaker()]);

        Assert.Contains(errors, error => error.Contains("quoted content", StringComparison.Ordinal));
    }

    [Fact]
    public void CreativeDirectionAssigningSpeechToOtherCharacter_IsRejected()
    {
        var speaker = Speaker();
        var other = Speaker();
        other.Id = 2;
        other.Name = "Teoman";
        other.CharacterKey = "teoman";
        var creative = Creative();
        creative.BodyMovement = "Teoman says something from off camera.";

        var errors = LtxNativeDialoguePromptComposer.ValidateCreativeDirectionResult(
            creative, [Dialogue("Merhaba.", speaker, 1)], [speaker, other]);

        Assert.Contains(errors, error => error.Contains("another character", StringComparison.Ordinal));
    }

    [Fact]
    public void FinalPreflightRejectsTamperedPrompt()
    {
        var result = Build("Merhaba.");

        var exception = Assert.Throws<LtxNativeDialogueFinalPromptValidationException>(() => _builder.Validate(new()
        {
            Prompt = result.CombinedPrompt.Replace(result.OnlySpeakerLine, string.Empty, StringComparison.Ordinal),
            SpeakerDisplayName = "Metehan",
            ExactDialogueLines = ["Merhaba."],
            VoiceDirection = result.VoiceDirection,
            VisualDirection = result.VisualDirection
        }));

        Assert.Contains("only-speaker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalPreflightRejectsPromptOverSafeLength()
    {
        var result = Build("Merhaba.");

        var exception = Assert.Throws<LtxNativeDialogueFinalPromptValidationException>(() => _builder.Validate(new()
        {
            Prompt = result.CombinedPrompt + new string('x', LtxNativeDialogueFinalPromptBuilder.MaxPromptCharacters),
            SpeakerDisplayName = "Metehan",
            ExactDialogueLines = ["Merhaba."],
            VoiceDirection = result.VoiceDirection,
            VisualDirection = result.VisualDirection
        }));

        Assert.Contains("safe 8000 character limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputDoesNotDependOnCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = Build("İIıi.").CombinedPrompt;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = Build("İIıi.").CombinedPrompt;

            Assert.Equal(turkish, english);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Scene36Fixture_UsesKaraVezirAndAuthoritativeDialogue()
    {
        const string exact = "Metehan, oyunun kırılganlığına karşı bir adım atacak. O, kendi düşmanı olacak.";
        var speaker = Speaker();
        speaker.Id = 29;
        speaker.CharacterKey = "kara_vezir";
        speaker.Name = "Kara Vezir";

        var result = Build(exact, speaker: speaker);

        Assert.Contains($"Kara Vezir says in Turkish: {LtxNativeDialogueFinalPromptBuilder.Quote(exact)}", result.CombinedPrompt, StringComparison.Ordinal);
        Assert.Contains("Only Kara Vezir speaks", result.CombinedPrompt, StringComparison.Ordinal);
    }

    private LtxNativeDialogueFinalPrompt Build(string exactDialogue,
        LtxNativeDialogueCreativeDirectionResult? creative = null,
        StoryCharacter? speaker = null) =>
        _builder.Build(Request(exactDialogue, speaker ?? Speaker(), creative));

    private static LtxNativeDialogueFinalPromptRequest Request(string exactDialogue, StoryCharacter speaker,
        LtxNativeDialogueCreativeDirectionResult? creative = null) => new()
    {
        VisualDirection = "Single continuous forest shot.",
        CreativeDirection = creative ?? Creative(),
        Speaker = speaker,
        VoiceProfile = Profile(speaker),
        Dialogue = [Dialogue(exactDialogue, speaker, 1)],
        ProjectLanguage = "Türkçe"
    };

    private static StoryCharacter Speaker() => new()
    {
        Id = 1,
        CharacterKey = "metehan",
        Name = "Metehan",
        Role = "hero"
    };

    private static SpeechDialogueLine Dialogue(string text, StoryCharacter speaker, int order) => new()
    {
        StoryCharacterId = speaker.Id,
        SpeakerKey = speaker.CharacterKey,
        SpeakerName = speaker.Name,
        SourceText = text,
        SpokenText = text,
        SortOrder = order
    };

    private static LtxNativeVoiceProfile Profile(StoryCharacter speaker) => new()
    {
        StoryCharacterId = speaker.Id,
        VoiceDescription = "warm Turkish voice",
        Language = "tr",
        SpeakingStyle = "calm",
        PerceivedAge = "young adult",
        GenderPresentation = "neutral",
        AccentDescription = "Istanbul Turkish",
        PitchDescription = "medium pitch",
        TempoDescription = "natural tempo",
        SettingsHash = "hash"
    };

    private static LtxNativeDialogueCreativeDirectionResult Creative() => new()
    {
        PerformanceDirection = "Restrained confidence.",
        FacialExpression = "Focused gaze.",
        BodyMovement = "One subtle step forward.",
        VoiceDeliveryDirection = "Calm measured delivery.",
        CameraDirection = "Slow stable push-in.",
        EnvironmentalMotion = "Leaves move gently.",
        TimingDirection = "Brief silence before and after.",
        Warnings = []
    };

    private static string ExpectedSnapshot() =>
        """
[Visual Direction]
Single continuous forest shot.
[Performance Direction]
Restrained confidence.; Focused gaze.; One subtle step forward.; Calm measured delivery.; Brief silence before and after.
[Camera Direction]
Slow stable push-in.; Leaves move gently.
[Voice Direction]
Project language: Türkçe; Voice: warm Turkish voice; Speaking style: calm; Perceived age: young adult; Gender presentation: neutral; Accent: Istanbul Turkish; Pitch: medium pitch; Tempo: natural tempo
[Authoritative Dialogue]
Metehan says in Turkish: "Merhaba."
Only Metehan speaks
[Native Dialogue Constraints]
Metehan speaks audibly in natural Turkish with clear Turkish pronunciation and synchronized lip movement.
No narrator. No additional dialogue. No background music.
No subtitles. No captions. No on-screen text.
single continuous shot, no cuts.
""";
}
