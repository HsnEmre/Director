using Director.Dtos.StoryGeneration;
using Director.Models;

namespace Director.Services;

public sealed class StoryPromptBuilder : Interfaces.IStoryPromptBuilder
{
    private const string SilentVideoRule =
        "Video clips will be generated without audio. Do not include narration, spoken dialogue, music, sound effects, ambient audio or lip-sync instructions inside the video prompt. Dialogue and narration must be stored separately for a later audio-production stage.";

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

    private static string Limit(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
