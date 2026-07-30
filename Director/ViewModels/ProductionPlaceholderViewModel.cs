using Director.Helpers;

namespace Director.ViewModels;

public sealed class ProductionPlaceholderViewModel : ObservableObject
{
    private int _filmProjectId;

    public int FilmProjectId
    {
        get => _filmProjectId;
        private set => SetProperty(ref _filmProjectId, value);
    }

    public void Initialize(int filmProjectId)
    {
        FilmProjectId = filmProjectId;
    }
}
