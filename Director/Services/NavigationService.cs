using Director.Helpers;
using Director.Services.Interfaces;
using Director.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Director.Services;

public sealed class NavigationService : ObservableObject, INavigationService
{
    private readonly IServiceProvider _serviceProvider;
    private object? _currentViewModel;
    private string _currentStep = "Proje Ayarları";
    private int? _currentProjectId;

    public NavigationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public object? CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetProperty(ref _currentViewModel, value);
    }

    public string CurrentStep
    {
        get => _currentStep;
        private set => SetProperty(ref _currentStep, value);
    }

    public int? CurrentProjectId
    {
        get => _currentProjectId;
        private set => SetProperty(ref _currentProjectId, value);
    }

    public async Task NavigateToProjectSetupAsync(int? projectId = null)
    {
        var viewModel = _serviceProvider.GetRequiredService<CreateFilmProjectViewModel>();
        if (projectId is int id)
        {
            await viewModel.LoadProjectAsync(id);
        }

        CurrentProjectId = projectId;
        CurrentStep = "Proje Ayarları";
        CurrentViewModel = viewModel;
    }

    public async Task NavigateToStoryGenerationAsync(int projectId)
    {
        var viewModel = _serviceProvider.GetRequiredService<StoryGenerationViewModel>();
        await viewModel.InitializeAsync(projectId);
        CurrentProjectId = projectId;
        CurrentStep = "Hikaye ve Sahne Planı";
        CurrentViewModel = viewModel;
    }

    public async Task NavigateToProjectHistoryAsync()
    {
        var viewModel = _serviceProvider.GetRequiredService<ProjectHistoryViewModel>();
        await viewModel.LoadAsync();
        CurrentStep = "Projelerim";
        CurrentViewModel = viewModel;
    }

    public async Task NavigateToProductionAsync(int projectId, int selectedTabIndex = 0)
    {
        var viewModel = _serviceProvider.GetRequiredService<ProductionWorkspaceViewModel>();
        viewModel.SelectedWorkspaceTabIndex = selectedTabIndex;
        await viewModel.InitializeAsync(projectId);
        CurrentProjectId = projectId;
        CurrentStep = "Üretim";
        CurrentViewModel = viewModel;
    }
}
