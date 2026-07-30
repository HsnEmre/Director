using Director.Dtos.StoryGeneration;
using Director.Models;

namespace Director.Services;

public sealed class StoryPromptBuilder : Interfaces.IStoryPromptBuilder
{
    private const string SilentVideoRule =
        "Video clips will be generated without audio. Do not include narration, spoken dialogue, music, sound effects, ambient audio or lip-sync instructions inside the video prompt. Dialogue and narration must be stored separately for a later audio-production stage.";

    public string BuildStoryBibleSystemPrompt()
    {
        return "You are a professional film story architect for an AI film production pipeline. Return only valid JSON matching the provided schema. Do not include markdown. Create a coherent story bible, character continuity bible and visual direction. Keep story, narration and dialogue language aligned with the user's selected language. Image and video prompts will be produced later in English.";
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
}
