using System.Text.Json;
using Director.Dtos.StoryGeneration;
using Director.Services;

namespace Director.Tests;

public sealed class StoryCharacterValidationTests
{
    [Fact]
    public void RoleWithPhysicalDescription_FailsValidation()
    {
        var bible = ValidBible();
        bible.Characters[0].Role = "Tall warrior with black hair and sharp eyes";

        var issues = StoryCharacterFieldValidator.ValidateIssues(bible);

        Assert.Contains(issues, issue => issue.FieldName == "role" && issue.Reason.Contains("appearance", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RoleWithClothingDescription_FailsValidation()
    {
        var bible = ValidBible();
        bible.Characters[0].Role = "Saraydaki kurt kurkuyle suslu koyu kahverengi deri zirh giymistir";

        var issues = StoryCharacterFieldValidator.ValidateIssues(bible);

        Assert.Contains(issues, issue => issue.FieldName == "role" && issue.Reason.Contains("clothing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShortNarrativeRole_PassesValidation()
    {
        var bible = ValidBible();
        bible.Characters[0].Role = "Protagonist";

        StoryCharacterFieldValidator.Validate(bible);
    }

    [Fact]
    public void DuplicateCharacterKey_FailsValidation()
    {
        var bible = ValidBible();
        bible.Characters.Add(new StoryCharacterResponse
        {
            CharacterKey = "metehan",
            Name = "Mete Han",
            Role = "Ruler",
            PhysicalDescription = "Commanding face and steady gaze.",
            ClothingDescription = "Simple ruler clothing and boots.",
            PersonalityDescription = "Disciplined.",
            VoiceDescription = "Clear Turkish voice.",
            ContinuityDescription = "Always visually consistent.",
            ForbiddenChanges = ["Do not change face."]
        });

        var issues = StoryCharacterFieldValidator.ValidateIssues(bible);

        Assert.Contains(issues, issue => issue.FieldName == "characterKey" && issue.Reason.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoryBibleSchema_ConstrainsRoleSeparatelyFromDescriptionFields()
    {
        var json = JsonSerializer.Serialize(StoryJsonSchemas.StoryBibleSchema());

        Assert.Contains("\"role\"", json);
        Assert.Contains("\"maxLength\":30", json);
        Assert.Contains("Short narrative function only", json);
        Assert.Contains("physicalDescription", json);
        Assert.Contains("clothingDescription", json);
    }

    [Fact]
    public void CharacterCorrectionSchema_IsTinyPatchAndDoesNotReturnStoryBible()
    {
        var json = JsonSerializer.Serialize(StoryJsonSchemas.StoryCharacterCorrectionsSchema());

        Assert.Contains("corrections", json);
        Assert.Contains("characterKey", json);
        Assert.Contains("field", json);
        Assert.Contains("value", json);
        Assert.DoesNotContain("synopsis", json);
        Assert.DoesNotContain("characters\"", json);
    }

    [Fact]
    public void ApplyCharacterCorrections_ChangesOnlyRequestedField()
    {
        var response = new StoryCharactersResponse { Characters = [ValidBible().Characters[0]] };
        var originalPhysical = response.Characters[0].PhysicalDescription;
        var corrections = new StoryCharacterCorrectionsResponse
        {
            Corrections =
            [
                new StoryCharacterFieldCorrectionResponse
                {
                    CharacterKey = "metehan",
                    Field = "role",
                    Value = "Ruler"
                }
            ]
        };

        StoryGenerationService.ApplyCharacterCorrections(
            response,
            corrections,
            [new StoryCharacterValidationIssue(0, "metehan", "role", 90, 30, "Role too long.")]);

        Assert.Equal("Ruler", response.Characters[0].Role);
        Assert.Equal(originalPhysical, response.Characters[0].PhysicalDescription);
    }

    [Fact]
    public void ThirtyScenesAtTenSeconds_TotalThreeHundredSeconds()
    {
        var durations = Enumerable.Range(1, 30).Select(_ => 10).ToList();

        Assert.Equal(30, durations.Count);
        Assert.Equal(300, durations.Sum());
    }

    private static StoryBibleResponse ValidBible()
    {
        return new StoryBibleResponse
        {
            Title = "Mete Han",
            Logline = "A ruler rises.",
            Synopsis = "A compact historical story.",
            OpeningSummary = "Opening.",
            DevelopmentSummary = "Development.",
            ClimaxSummary = "Climax.",
            EndingSummary = "Ending.",
            WorldDescription = "Ancient steppe world.",
            VisualDirection = "Cinematic realism.",
            ContinuityRules = ["Keep character identity stable."],
            Characters =
            [
                new StoryCharacterResponse
                {
                    CharacterKey = "metehan",
                    Name = "Mete Han",
                    Role = "Protagonist",
                    PhysicalDescription = "Young ruler with a focused face and steady gaze.",
                    ClothingDescription = "Dark leather armor, fur cloak and boots.",
                    PersonalityDescription = "Disciplined and brave.",
                    VoiceDescription = "Natural Turkish voice.",
                    ContinuityDescription = "Preserve face, age and clothing.",
                    ForbiddenChanges = ["Do not change face.", "Do not change clothing."]
                }
            ]
        };
    }
}
