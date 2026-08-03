namespace Director.Services;

public sealed class ProjectGenerationAlreadyRunningException : InvalidOperationException
{
    public const string UserMessage = "Bu proje başka bir Director işlemi tarafından üretiliyor. İşlem tamamlandıktan sonra yeniden deneyin.";

    public ProjectGenerationAlreadyRunningException(int filmProjectId, string databaseIdentityShortHash)
        : base(UserMessage)
    {
        FilmProjectId = filmProjectId;
        DatabaseIdentityShortHash = databaseIdentityShortHash;
    }

    public int FilmProjectId { get; }
    public string DatabaseIdentityShortHash { get; }
}
