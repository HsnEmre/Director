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
                        ["characterKey"] = StringSchema(),
                        ["name"] = StringSchema(),
                        ["role"] = StringSchema(),
                        ["physicalDescription"] = StringSchema(),
                        ["clothingDescription"] = StringSchema(),
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

    private static object StringSchema() => new { type = "string" };

    private static object StringArraySchema() => new
    {
        type = "array",
        items = new { type = "string" }
    };
}
