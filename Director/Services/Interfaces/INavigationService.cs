namespace Director.Services.Interfaces;

public interface INavigationService
{
    object? CurrentViewModel { get; }
    string CurrentStep { get; }
    int? CurrentProjectId { get; }

    Task NavigateToProjectSetupAsync(int? projectId = null);
    Task NavigateToStoryGenerationAsync(int projectId);
    Task NavigateToProjectHistoryAsync();
    Task NavigateToProductionAsync(int projectId, int selectedTabIndex = 0);
}
