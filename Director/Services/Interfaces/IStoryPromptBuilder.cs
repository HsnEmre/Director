using Director.Dtos.StoryGeneration;
using Director.Models;

namespace Director.Services.Interfaces;

public interface IStoryPromptBuilder
{
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
