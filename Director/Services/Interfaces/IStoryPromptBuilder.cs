using Director.Dtos.StoryGeneration;
using Director.Models;
using Director.Services;

namespace Director.Services.Interfaces;

public interface IStoryPromptBuilder
{
    string BuildStoryNarrativeSystemPrompt();
    string BuildStoryNarrativeUserPrompt(FilmProject project);
    string BuildCharacterGenerationSystemPrompt();
    string BuildCharacterGenerationUserPrompt(FilmProject project, FilmStory story);
    string BuildCharacterCorrectionSystemPrompt();
    string BuildCharacterCorrectionUserPrompt(IReadOnlyList<StoryCharacterResponse> characters, IReadOnlyList<StoryCharacterValidationIssue> issues);
    string BuildNarrativeSceneSystemPrompt();
    string BuildNarrativeSceneUserPrompt(
        FilmProject project,
        FilmStory story,
        int sceneNumber,
        string previousSceneContext);
    string BuildImagePromptSystemPrompt();
    string BuildImagePromptUserPrompt(
        FilmProject project,
        FilmStory story,
        FilmScene scene);
    string BuildVideoPromptSystemPrompt();
    string BuildVideoPromptUserPrompt(
        FilmProject project,
        FilmStory story,
        FilmScene scene,
        string imagePromptContext);

    string BuildStoryBibleSystemPrompt();
    string BuildStoryBibleUserPrompt(FilmProject project);
    string BuildStoryBibleConciseUserPrompt(FilmProject project);

    string BuildSceneOutlineSystemPrompt();
    string BuildSceneOutlineUserPrompt(
        FilmProject project,
        FilmStory story,
        int startScene,
        int endScene,
        string? previousSceneContext);

    string BuildScenePackageSystemPrompt();
    string BuildScenePackageUserPrompt(
        FilmProject project,
        FilmStory story,
        IReadOnlyList<SceneOutlineItemDto> scenes,
        string? previousSceneContext);

    string BuildSingleScenePackageSystemPrompt();
    string BuildSingleScenePackageUserPrompt(
        FilmProject project,
        FilmStory story,
        int sceneNumber,
        string previousSceneContext);
}
