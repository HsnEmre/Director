namespace Director.Dtos.StoryGeneration;

public static class StoryJsonSchemas
{
    public static object StoryBibleSchema() => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "title", "logline", "synopsis", "openingSummary", "developmentSummary", "climaxSummary", "endingSummary", "worldDescription", "visualDirection", "continuityRules", "characters" },
        properties = new Dictionary<string, object>
        {
            ["title"] = StringSchema(),
            ["logline"] = StringSchema(),
            ["synopsis"] = StringSchema(),
            ["openingSummary"] = StringSchema(),
            ["developmentSummary"] = StringSchema(),
            ["climaxSummary"] = StringSchema(),
            ["endingSummary"] = StringSchema(),
            ["worldDescription"] = StringSchema(),
            ["visualDirection"] = StringSchema(),
            ["continuityRules"] = StringArraySchema(),
            ["characters"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "characterKey", "name", "role", "physicalDescription", "clothingDescription", "personalityDescription", "voiceDescription", "continuityDescription", "forbiddenChanges" },
                    properties = new Dictionary<string, object>
                    {
                        ["characterKey"] = StringSchema(80, "Stable lowercase identifier such as metehan or commander_1."),
                        ["name"] = StringSchema(160, "Character display name."),
                        ["role"] = RoleSchema(),
                        ["physicalDescription"] = StringSchema(null, "Appearance only: face, body, age, hair, eyes and other physical traits."),
                        ["clothingDescription"] = StringSchema(null, "Clothing and equipment only: garments, armor, accessories, weapons and carried items."),
                        ["personalityDescription"] = StringSchema(),
                        ["voiceDescription"] = StringSchema(),
                        ["continuityDescription"] = StringSchema(),
                        ["forbiddenChanges"] = StringArraySchema()
                    }
                }
            }
        }
    };

    public static object SceneOutlineBatchSchema() => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "scenes" },
        properties = new Dictionary<string, object>
        {
            ["scenes"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "sceneNumber", "title", "storyBeat", "shortDescription", "characters", "location", "timeOfDay", "continuityFromPreviousScene" },
                    properties = new Dictionary<string, object>
                    {
                        ["sceneNumber"] = new { type = "integer" },
                        ["title"] = StringSchema(),
                        ["storyBeat"] = StringSchema(),
                        ["shortDescription"] = StringSchema(),
                        ["characters"] = StringArraySchema(),
                        ["location"] = StringSchema(),
                        ["timeOfDay"] = StringSchema(),
                        ["continuityFromPreviousScene"] = StringSchema()
                    }
                }
            }
        }
    };

    public static object ScenePackageBatchSchema() => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "scenes" },
        properties = new Dictionary<string, object>
        {
            ["scenes"] = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    required = new[] { "sceneNumber", "title", "storyBeat", "sceneDescription", "locationDescription", "timeOfDay", "characters", "continuityFromPreviousScene", "imagePrompt", "imageNegativePrompt", "videoPrompt", "videoNegativePrompt", "narrationText", "dialogue", "validationChecklist" },
                    properties = new Dictionary<string, object>
                    {
                        ["sceneNumber"] = new { type = "integer" },
                        ["title"] = StringSchema(),
                        ["storyBeat"] = StringSchema(),
                        ["sceneDescription"] = StringSchema(),
                        ["locationDescription"] = StringSchema(),
                        ["timeOfDay"] = StringSchema(),
                        ["characters"] = StringArraySchema(),
                        ["continuityFromPreviousScene"] = StringSchema(),
                        ["imagePrompt"] = StringSchema(),
                        ["imageNegativePrompt"] = StringSchema(),
                        ["videoPrompt"] = StringSchema(),
                        ["videoNegativePrompt"] = StringSchema(),
                        ["narrationText"] = StringSchema(),
                        ["dialogue"] = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                required = new[] { "characterKey", "characterName", "text" },
                                properties = new Dictionary<string, object>
                                {
                                    ["characterKey"] = StringSchema(),
                                    ["characterName"] = StringSchema(),
                                    ["text"] = StringSchema()
                                }
                            }
                        },
                        ["validationChecklist"] = StringArraySchema()
                    }
                }
            }
        }
    };

    public static object SingleScenePackageSchema() => new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "sceneNumber", "durationSeconds", "title", "storyBeat", "sceneDescription", "locationDescription", "timeOfDay", "characters", "imagePrompt", "imageNegativePrompt", "videoPrompt", "videoNegativePrompt", "narrationText", "dialogueJson", "continuityFromPreviousScene", "validationChecklist" },
        properties = new Dictionary<string, object>
        {
            ["sceneNumber"] = new { type = "integer" },
            ["durationSeconds"] = new { type = "integer" },
            ["title"] = StringSchema(),
            ["storyBeat"] = StringSchema(),
            ["sceneDescription"] = StringSchema(),
            ["locationDescription"] = StringSchema(),
            ["timeOfDay"] = StringSchema(),
            ["characters"] = StringArraySchema(),
            ["imagePrompt"] = StringSchema(),
            ["imageNegativePrompt"] = StringSchema(),
            ["videoPrompt"] = StringSchema(),
            ["videoNegativePrompt"] = StringSchema(),
            ["narrationText"] = StringSchema(),
            ["dialogueJson"] = StringSchema(),
            ["continuityFromPreviousScene"] = StringSchema(),
            ["validationChecklist"] = StringArraySchema()
        }
    };

    private static object StringSchema(int? maxLength = null, string? description = null)
    {
        var schema = new Dictionary<string, object?>
        {
            ["type"] = "string"
        };
        if (maxLength is int max)
        {
            schema["maxLength"] = max;
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            schema["description"] = description;
        }

        return schema;
    }

    private static object RoleSchema() => new
    {
        type = "string",
        maxLength = 80,
        description = "Short narrative function only. Examples: Protagonist, Ruler, Warrior Ally, Commander, Political Antagonist. Never include appearance, clothing, equipment, personality paragraph or scene description."
    };

    private static object StringArraySchema() => new
    {
        type = "array",
        items = new { type = "string" }
    };
}
