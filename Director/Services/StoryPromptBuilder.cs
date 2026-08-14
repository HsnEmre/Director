using Director.Dtos.StoryGeneration;
using Director.Models;

namespace Director.Services;

public sealed class StoryPromptBuilder : Interfaces.IStoryPromptBuilder
{
    private const string SilentVideoRule =
        "Video clips will be generated without audio. Do not include narration, spoken dialogue, music, sound effects, ambient audio or lip-sync instructions inside the video prompt. Dialogue and narration must be stored separately for a later audio-production stage.";

    private const string JsonOnlyStageRule =
        "Return only valid JSON matching the supplied schema. Do not include markdown, code fences, explanations or extra fields. Keep this stage separate; do not produce fields owned by later stages.";

    public string BuildStoryNarrativeSystemPrompt()
    {
        return "You are a professional film story architect for a staged AI film production pipeline. " + JsonOnlyStageRule + " Produce only the story narrative fields. Do not produce characters, scene plans, image prompts, video prompts, dialogue, narration or audio instructions.";
    }

    public string BuildStoryNarrativeUserPrompt(FilmProject project)
    {
        return $"""
Create the story narrative checkpoint for this film project.
Project name: {project.ProjectName}
Subject: {project.Subject}
Total duration minutes: {project.TotalDurationMinutes}
Clip duration seconds: {project.ClipDurationSeconds}
Exact scene count required later: {project.CalculatedClipCount}
Language for title, logline and synopsis: {project.Language}
Target audience: {project.TargetAudience}
Genre: {project.StoryGenre}
Visual style: {project.VisualStyle}
Video style: {project.VideoStyle}
Aspect ratio: {project.AspectRatio}
Resolution: {project.Resolution}
Use narrator: {project.UseNarrator}
Narrator tone: {project.NarratorTone}
Main character notes: {project.MainCharacterDescription}
Additional instructions: {project.AdditionalInstructions}

Return only: title, logline, synopsis, openingSummary, developmentSummary, climaxSummary, endingSummary, worldDescription, visualDirection, continuityRules.
Do not return a characters array. Do not return scenes or media prompts.
""";
    }

    public string BuildCharacterGenerationSystemPrompt()
    {
        return "You are creating a compact character continuity bible for a staged AI film pipeline. " + JsonOnlyStageRule + " Produce only characters. Role must be a short narrative function, maximum 30 characters. Put appearance only in physicalDescription and clothing/equipment only in clothingDescription.";
    }

    public string BuildCharacterGenerationUserPrompt(FilmProject project, FilmStory story)
    {
        return $"""
Create the character continuity checkpoint for this already-saved story.
Project: {project.ProjectName}
Subject: {Limit(project.Subject, 700)}
Story title: {story.Title}
Logline: {story.Logline}
Synopsis: {story.Synopsis}
World: {story.WorldDescription}
Visual direction: {story.VisualDirection}
Main character notes: {Limit(project.MainCharacterDescription, 500)}
Additional instructions: {Limit(project.AdditionalInstructions, 600)}

Return only a characters array.
If the film has no real human, animal or creature character, return an empty characters array.
Do not invent characters for objects, locations, weather, lights, vehicles, buildings, landscapes or atmosphere.
For each character, role is only a story function such as Protagonist, Ruler, Ally, Commander, Antagonist. Maximum 30 characters, no sentence, no appearance, no clothing.
""";
    }

    public string BuildCharacterCorrectionSystemPrompt()
    {
        return "You repair character field validation issues by returning a tiny JSON patch only. " + JsonOnlyStageRule + " Each correction must include only characterKey, field and value. Do not return the full story, full character list or unchanged fields.";
    }

    public string BuildCharacterCorrectionUserPrompt(
        IReadOnlyList<StoryCharacterResponse> characters,
        IReadOnlyList<StoryCharacterValidationIssue> issues)
    {
        var characterText = string.Join(Environment.NewLine, characters.Select(character =>
            $"{character.CharacterKey}: role={Limit(character.Role, 160)} | physical={Limit(character.PhysicalDescription, 220)} | clothing={Limit(character.ClothingDescription, 220)}"));
        var issueText = string.Join(Environment.NewLine, issues.Select(issue =>
            $"- characterKey={issue.CharacterKey}; field={issue.FieldName}; length={issue.ActualLength}/{issue.MaxLength}; reason={issue.Reason}"));

        return $"""
Repair only the invalid fields listed below.
Current affected characters:
{characterText}

Validation issues:
{issueText}

Allowed fields: characterKey, name, role, physicalDescription, clothingDescription, personalityDescription, voiceDescription, continuityDescription.
Role must be a short narrative function, maximum 30 characters.
Move appearance details to physicalDescription. Move clothing, armor, weapons and equipment details to clothingDescription.
Return only corrections. Do not include unchanged fields.
""";
    }

    public string BuildNarrativeSceneSystemPrompt()
    {
        return "You create one narrative scene checkpoint for a staged AI film pipeline. " + JsonOnlyStageRule + " Do not produce image prompts, video prompts, narration text, dialogue lines, sound, music or audio instructions. Technical fields must be in English.";
    }

    public string BuildNarrativeSceneUserPrompt(
        FilmProject project,
        FilmStory story,
        int sceneNumber,
        string previousSceneContext)
    {
        var characters = string.Join(Environment.NewLine, story.Characters
            .OrderBy(character => character.SortOrder)
            .Take(8)
            .Select(character => $"{character.CharacterKey}: {character.Name}, {character.Role}. Continuity: {Limit(character.ContinuityDescription, 180)}"));
        var storySection = SelectStorySection(story, sceneNumber, project.CalculatedClipCount);
        var continuityRule = sceneNumber == 1
            ? $"continuityFromPreviousScene must be exactly \"{StoryGenerationService.OpeningSceneContinuityFromPreviousScene}\"."
            : "continuityFromPreviousScene is required and must briefly describe a concrete visual, spatial, temporal or action link from the previous scene.";

        return $"""
Create only narrative scene {sceneNumber} of {project.CalculatedClipCount}.
Target duration seconds: {project.ClipDurationSeconds}
Project subject: {Limit(project.Subject, 300)}
Story title: {Limit(story.Title, 160)}
Synopsis: {Limit(story.Synopsis, 700)}
Relevant story section: {Limit(storySection, 700)}
World: {Limit(story.WorldDescription, 500)}
Visual direction: {Limit(story.VisualDirection, 500)}
Scene position: {DescribeScenePosition(sceneNumber, project.CalculatedClipCount)}
Previous scene context: {Limit(previousSceneContext, 500)}
Continuity contract: {continuityRule}
Allowed characters:
{characters}

Return only sceneNumber, durationSeconds, title, storyBeat, sceneDescription, locationDescription, timeOfDay, characters, continuityFromPreviousScene and dialogueIntent.
Do not include imagePrompt, videoPrompt, dialogueJson, narrationText or validationChecklist.
""";
    }

    public string BuildImagePromptSystemPrompt()
    {
        return "You create a single image prompt after all narrative scenes are saved. " + JsonOnlyStageRule + " Return only sceneNumber, imagePrompt and imageNegativePrompt. Technical prompt fields must be English.";
    }

    public string BuildImagePromptUserPrompt(FilmProject project, FilmStory story, FilmScene scene)
    {
        var characterDetails = string.Join(Environment.NewLine, story.Characters
            .OrderBy(character => character.SortOrder)
            .Where(character => CharacterListContains(scene.CharactersJson, character.CharacterKey))
            .Select(character => $"{character.CharacterKey}: {character.Name}. Physical: {Limit(character.PhysicalDescription, 220)} Clothing: {Limit(character.ClothingDescription, 220)} Continuity: {Limit(character.ContinuityDescription, 160)}"));

        return $"""
Create the image prompt for scene {scene.SceneNumber}.
Project: {project.ProjectName}
Visual style: {Limit(project.VisualStyle, 300)}
Aspect ratio/resolution: {project.AspectRatio}; {project.Resolution}
Story title: {story.Title}
World: {Limit(story.WorldDescription, 500)}
Visual direction: {Limit(story.VisualDirection, 500)}
Scene title: {scene.Title}
Story beat: {scene.StoryBeat}
Scene description: {scene.SceneDescription}
Location: {scene.LocationDescription}
Time of day: {scene.TimeOfDay}
Continuity from previous scene: {scene.ContinuityFromPreviousScene}
Characters in scene:
{characterDetails}

The imagePrompt must describe one fixed film frame: subjects, physical traits, clothing, setting, lighting, camera angle, lens/framing, mood and continuity.
The imageNegativePrompt must be short comma-separated terms only.
""";
    }

    public string BuildVideoPromptSystemPrompt()
    {
        return "You create a single image-to-video motion prompt after image prompts are saved and before image generation starts. " + JsonOnlyStageRule + " Return only sceneNumber, videoPrompt, videoNegativePrompt, startState, motionPlan and endState. Do not include audio, sound, narration, dialogue, music or lip-sync instructions.";
    }

    public string BuildVideoPromptUserPrompt(
        FilmProject project,
        FilmStory story,
        FilmScene scene,
        string imagePromptContext)
    {
        return $"""
Create the video prompt for scene {scene.SceneNumber} from the narrative scene and saved image prompt context.
Project: {project.ProjectName}
Video style: {Limit(project.VideoStyle, 300)}
Clip duration seconds: {project.ClipDurationSeconds}
Story title: {story.Title}
Synopsis: {Limit(story.Synopsis, 600)}
Scene title: {scene.Title}
Story beat: {scene.StoryBeat}
Scene description: {scene.SceneDescription}
Location: {scene.LocationDescription}
Time of day: {scene.TimeOfDay}
Continuity from previous scene: {scene.ContinuityFromPreviousScene}
Existing image prompt: {Limit(scene.ImagePrompt, 900)}
Existing image negative prompt: {Limit(scene.ImageNegativePrompt, 500)}
Image prompt context: {imagePromptContext}

Plan motion for the still frame described by the image prompt. Do not depend on an already generated image.
Preserve character identity, clothing, proportions, lighting, background layout and composition described in the image prompt.
The videoPrompt must describe visible motion, facial/gaze/body changes, environmental motion, one camera movement, pace and final position.
The videoNegativePrompt must be short comma-separated terms only.
""";
    }

    public string BuildStoryBibleSystemPrompt()
    {
        return "You are a professional film story architect for an AI film production pipeline. Return only valid JSON matching the provided schema. Do not include markdown. Create a coherent story bible, character continuity bible and visual direction. Keep story, narration and dialogue language aligned with the user's selected language. Image and video prompts will be produced later in English. For every character, role contains only a short narrative role such as Protagonist, Ruler, Warrior Ally, Commander, or Political Antagonist. Never place appearance, clothing or equipment details in role. physicalDescription contains appearance. clothingDescription contains clothing and equipment.";
    }

    public string BuildStoryBibleUserPrompt(FilmProject project)
    {
        return $"""
Create the story bible for this film project.
Project name: {project.ProjectName}
Subject: {project.Subject}
Total duration minutes: {project.TotalDurationMinutes}
Clip duration seconds: {project.ClipDurationSeconds}
Exact scene count required later: {project.CalculatedClipCount}
Language for title, logline, synopsis, narration and dialogue: {project.Language}
Target audience: {project.TargetAudience}
Genre: {project.StoryGenre}
Visual style: {project.VisualStyle}
Video style: {project.VideoStyle}
Aspect ratio: {project.AspectRatio}
Resolution: {project.Resolution}
Use narrator: {project.UseNarrator}
Narrator tone: {project.NarratorTone}
Main character notes: {project.MainCharacterDescription}
Additional instructions: {project.AdditionalInstructions}
Define clear continuity rules. Character keys must be stable, short, lowercase identifiers.
For each character: role must be a short story function only, maximum 80 characters, not a sentence. Put face/body/age/physical traits in physicalDescription. Put clothing, armor, fur, leather, weapons and carried equipment in clothingDescription. Never mix those details into role.
""";
    }

    public string BuildStoryBibleConciseUserPrompt(FilmProject project)
    {
        return $"""
Create a concise story bible for this very small silent visual film project.
Project name: {project.ProjectName}
Subject: {Limit(project.Subject, 500)}
Total duration minutes: {project.TotalDurationMinutes}
Clip duration seconds: {project.ClipDurationSeconds}
Exact scene count required later: {project.CalculatedClipCount}
Language for title, logline and synopsis: {project.Language}
Target audience: {project.TargetAudience}
Genre: {project.StoryGenre}
Visual style: {Limit(project.VisualStyle, 220)}
Video style: {Limit(project.VideoStyle, 220)}
Aspect ratio: {project.AspectRatio}
Resolution: {project.Resolution}
Use narrator: {project.UseNarrator}
Main character notes: {Limit(project.MainCharacterDescription, 240)}
Additional instructions: {Limit(project.AdditionalInstructions, 360)}

Return valid JSON only. No markdown, no code fences, no explanations.
Keep title under 12 words, logline under 30 words, synopsis under 80 words.
Keep openingSummary, developmentSummary, climaxSummary and endingSummary under 35 words each.
Keep worldDescription under 60 words and visualDirection under 100 words.
Return at most 3 short continuityRules.
If the subject has no real human, animal or creature character, return "characters": [].
Do not invent characters for objects, streets, weather, lights, vehicles, buildings, landscapes or atmosphere.
If a real character is explicitly required, return at most 1 character. Keep every character description concise and role under 6 words.
No narrator, no dialogue, no speech, no lip-sync and no audio beats.
""";
    }

    public string BuildSceneOutlineSystemPrompt()
    {
        return "You are planning scene outlines for a structured AI film pipeline. Return only valid JSON matching the provided schema. Do not include markdown. The number of scenes and scene numbers must exactly match the requested range. All technical scene descriptions, scene titles, storyBeat, location, timeOfDay, continuity instructions and later production-facing fields must be written in English, regardless of the story language. Narration and dialogue are the only fields that may use the project's selected language.";
    }

    public string BuildSceneOutlineUserPrompt(
        FilmProject project,
        FilmStory story,
        int startScene,
        int endScene,
        string? previousSceneContext)
    {
        return $"""
Create concise scene outlines for scenes {startScene} through {endScene}, inclusive.
Project: {project.ProjectName}
Language: {project.Language}
Exact total film scene count: {project.CalculatedClipCount}
Every scene duration seconds: {project.ClipDurationSeconds}
Story title: {story.Title}
Logline: {story.Logline}
Synopsis: {story.Synopsis}
Opening: {story.OpeningSummary}
Development: {story.DevelopmentSummary}
Climax: {story.ClimaxSummary}
Ending: {story.EndingSummary}
World: {story.WorldDescription}
Visual direction: {story.VisualDirection}
Previous scene context: {previousSceneContext}
Return exactly {endScene - startScene + 1} scenes. Do not skip or duplicate scene numbers.
""";
    }

    public string BuildScenePackageSystemPrompt()
    {
        return $"""
You are creating detailed scene packages for an AI image/video production pipeline. Return only valid JSON matching the provided schema. Do not include markdown. {SilentVideoRule}
All technical scene descriptions, scene title, storyBeat, sceneDescription, locationDescription, timeOfDay, continuityFromPreviousScene, imagePrompt, imageNegativePrompt, videoPrompt, videoNegativePrompt and validationChecklist must be written in English, regardless of the story language.
Only narrationText and dialogue may use the selected project language.
Each imagePrompt must describe one fixed film frame, repeat character physical traits and clothing, location, time, lighting, camera angle, lens/framing, visual style, aspect-ratio logic and continuity details. Avoid vague terms like same character.
Each videoPrompt must animate the reference image only: character movement, facial/gaze motion, body motion, camera movement, environmental motion, pace, end position, identity/clothing preservation, background preservation, no sudden transition and no new objects. Do not mention audio absence as a technical command; simply omit all audio content.
Base image negative prompt: text, subtitles, watermark, logo, signature, malformed anatomy, extra limbs, extra fingers, duplicate character, distorted face, inconsistent clothing, incorrect colors, low resolution, blurry, cropped head, deformed hands.
Base video negative prompt: scene transition, sudden camera jump, identity change, face morphing, clothing change, duplicated limbs, extra characters, flickering, background warping, object teleportation, extreme motion, camera shake, text, subtitles, watermark, logo.
""";
    }

    public string BuildScenePackageUserPrompt(
        FilmProject project,
        FilmStory story,
        IReadOnlyList<SceneOutlineItemDto> scenes,
        string? previousSceneContext)
    {
        var outlineText = string.Join(Environment.NewLine, scenes.Select(scene =>
            $"{scene.SceneNumber}. {scene.Title} | {scene.StoryBeat} | {scene.ShortDescription} | characters: {string.Join(", ", scene.Characters)} | location: {scene.Location} | time: {scene.TimeOfDay} | continuity: {scene.ContinuityFromPreviousScene}"));

        return $"""
Create detailed scene packages for the following outlines.
Project: {project.ProjectName}
Language for narration/dialogue only: {project.Language}
Write all technical production fields in English: scene title, storyBeat, sceneDescription, locationDescription, timeOfDay, continuityFromPreviousScene, imagePrompt, imageNegativePrompt, videoPrompt, videoNegativePrompt, validationChecklist.
Target audience: {project.TargetAudience}
Genre: {project.StoryGenre}
Visual style: {project.VisualStyle}
Video style: {project.VideoStyle}
Aspect ratio: {project.AspectRatio}
Resolution: {project.Resolution}
Every scene duration seconds: {project.ClipDurationSeconds}
Story title: {story.Title}
World: {story.WorldDescription}
Visual direction: {story.VisualDirection}
Continuity rules JSON: {story.ContinuityRulesJson}
Previous scene context: {previousSceneContext}
Outlines:
{outlineText}
Return exactly {scenes.Count} scenes with matching sceneNumber values.
""";
    }

    public string BuildSingleScenePackageSystemPrompt()
    {
        return "You create one scene package for an AI film pipeline. Return only valid JSON matching the schema. " + SilentVideoRule + "\n" +
            "All technical fields must be in English. DialogueJson may contain Turkish dialogue when the story beat needs speech.\n" +
            "Do not use narrationText; return an empty string for narrationText.\n" +
            "ImagePrompt and VideoPrompt must be concise but production-ready.\n" +
            "imageNegativePrompt and videoNegativePrompt must be short comma-separated unique terms only; never repeat a term or phrase.\n" +
            $"For scene 1, continuityFromPreviousScene must be exactly \"{StoryGenerationService.OpeningSceneContinuityFromPreviousScene}\". For scene 2 and later, continuityFromPreviousScene must briefly describe concrete visual, spatial, temporal or action continuity from the previous scene.\n" +
            "Keep title/timeOfDay under 120 characters; descriptions, beats, prompts, negatives, continuity and dialogueJson under 900 characters each.\n" +
            "DialogueJson must be a JSON array string, for example [] or [{\"characterKey\":\"metehan\",\"characterName\":\"Mete Han\",\"text\":\"...\"}].";
    }

    public string BuildSingleScenePackageUserPrompt(
        FilmProject project,
        FilmStory story,
        int sceneNumber,
        string previousSceneContext)
    {
        var characters = string.Join(Environment.NewLine, story.Characters
            .OrderBy(character => character.SortOrder)
            .Take(5)
            .Select(character => $"{character.CharacterKey}: {character.Name}, {character.Role}. Physical: {Limit(character.PhysicalDescription, 120)} Clothing: {Limit(character.ClothingDescription, 120)} Continuity: {Limit(character.ContinuityDescription, 120)}"));

        var storySection = SelectStorySection(story, sceneNumber, project.CalculatedClipCount);
        var continuityRule = sceneNumber == 1
            ? $"continuityFromPreviousScene: exactly \"{StoryGenerationService.OpeningSceneContinuityFromPreviousScene}\"."
            : "continuityFromPreviousScene: write one short concrete link to the previous scene's visual, spatial, temporal or action ending.";

        return $"""
Create only scene {sceneNumber} of {project.CalculatedClipCount}.
Project subject: {Limit(project.Subject, 240)}
Story title: {Limit(story.Title, 160)}
Short synopsis: {Limit(story.Synopsis, 600)}
Relevant story section: {Limit(storySection, 700)}
Scene position: {DescribeScenePosition(sceneNumber, project.CalculatedClipCount)}
Story beat target: advance the relevant story section by one distinct beat appropriate for scene {sceneNumber}; do not repeat the previous scene.
Target duration seconds: {project.ClipDurationSeconds}
Previous continuity: {Limit(previousSceneContext, 500)}
Continuity contract: {continuityRule}
Visual style: {Limit(project.VisualStyle, 220)}
Video style: {Limit(project.VideoStyle, 220)}
Language: technical fields in English; dialogue text in {project.Language}.
Characters allowed in this scene:
{characters}

Return one JSON object for scene {sceneNumber}. Do not include scenes before or after it.
""";
    }

    private static string SelectStorySection(FilmStory story, int sceneNumber, int totalScenes)
    {
        var ratio = totalScenes <= 0 ? 0 : sceneNumber / (double)totalScenes;
        return ratio switch
        {
            <= 0.2 => story.OpeningSummary,
            <= 0.7 => story.DevelopmentSummary,
            <= 0.9 => story.ClimaxSummary,
            _ => story.EndingSummary
        };
    }

    private static string DescribeScenePosition(int sceneNumber, int totalScenes)
    {
        var ratio = totalScenes <= 0 ? 0 : sceneNumber / (double)totalScenes;
        return ratio switch
        {
            < 0.2 => "opening setup",
            < 0.45 => "rising action",
            < 0.75 => "middle escalation",
            < 0.9 => "climax approach",
            _ => "ending resolution"
        };
    }

    private static bool CharacterListContains(string charactersJson, string characterKey) =>
        !string.IsNullOrWhiteSpace(characterKey) &&
        charactersJson.Contains(characterKey, StringComparison.OrdinalIgnoreCase);

    private static string Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
