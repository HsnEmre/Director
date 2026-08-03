namespace Director.Services;

public sealed class StorySceneGenerationException : InvalidOperationException
{
    public StorySceneGenerationException(
        int filmProjectId,
        int sceneNumber,
        string stage,
        string logPath,
        Exception innerException)
        : base($"Sahne {sceneNumber} icin model cevabi dogrulanamadi.", innerException)
    {
        FilmProjectId = filmProjectId;
        SceneNumber = sceneNumber;
        Stage = stage;
        LogPath = logPath;
    }

    public int FilmProjectId { get; }
    public int SceneNumber { get; }
    public string Stage { get; }
    public string LogPath { get; }
}
