using System.ComponentModel;
using System.Windows.Input;
using Director.Commands;
using Director.Helpers;
using Director.Services;
using Director.Services.Interfaces;

namespace Director.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IApplicationActivityCenter _activityCenter;

    public MainViewModel(INavigationService navigationService, IApplicationActivityCenter activityCenter)
    {
        _navigationService = navigationService;
        _activityCenter = activityCenter;
        if (_navigationService is NavigationService observableNavigation)
        {
            observableNavigation.PropertyChanged += OnNavigationPropertyChanged;
        }
        _activityCenter.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ActivitySummary));
            OnPropertyChanged(nameof(ActivityProgress));
        };

        NavigateProjectSetupCommand = new AsyncRelayCommand(() => _navigationService.NavigateToProjectSetupAsync(_navigationService.CurrentProjectId));
        NavigateProjectHistoryCommand = new AsyncRelayCommand(_navigationService.NavigateToProjectHistoryAsync);
        NavigateStoryCommand = new AsyncRelayCommand(NavigateStoryAsync, () => _navigationService.CurrentProjectId is not null);
        NavigateProductionCommand = new AsyncRelayCommand(NavigateProductionAsync, () => _navigationService.CurrentProjectId is not null);
    }

    public object? CurrentViewModel => _navigationService.CurrentViewModel;
    public string CurrentStep => _navigationService.CurrentStep;
    public int? CurrentProjectId => _navigationService.CurrentProjectId;
    public double ActivityProgress => _activityCenter.Snapshot.Progress;
    public string ActivitySummary
    {
        get
        {
            var snapshot = _activityCenter.Snapshot;
            if (string.IsNullOrWhiteSpace(snapshot.OperationName) && string.IsNullOrWhiteSpace(snapshot.CurrentPhase))
            {
                return $"WanGP MCP: {snapshot.McpState}";
            }

            var elapsed = snapshot.StartedAt is DateTime startedAt
                ? DateTime.Now - startedAt
                : TimeSpan.Zero;
            var scene = snapshot.SceneNumber is int sceneNumber ? $" Sahne {sceneNumber}" : string.Empty;
            var step = snapshot.CurrentStep is int current && snapshot.TotalSteps is int total
                ? $" Adim {current}/{total}"
                : string.Empty;
            return $"{snapshot.OperationName}{scene} - {snapshot.CurrentPhase}{step} - {elapsed:mm\\:ss}";
        }
    }

    public ICommand NavigateProjectSetupCommand { get; }
    public ICommand NavigateProjectHistoryCommand { get; }
    public ICommand NavigateStoryCommand { get; }
    public ICommand NavigateProductionCommand { get; }

    public Task InitializeAsync()
    {
        return _navigationService.NavigateToProjectSetupAsync();
    }

    private Task NavigateStoryAsync()
    {
        return _navigationService.CurrentProjectId is int projectId
            ? _navigationService.NavigateToStoryGenerationAsync(projectId)
            : Task.CompletedTask;
    }

    private Task NavigateProductionAsync()
    {
        return _navigationService.CurrentProjectId is int projectId
            ? _navigationService.NavigateToProductionAsync(projectId)
            : Task.CompletedTask;
    }

    private void OnNavigationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CurrentViewModel));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentProjectId));

        if (NavigateStoryCommand is AsyncRelayCommand storyCommand)
        {
            storyCommand.RaiseCanExecuteChanged();
        }

        if (NavigateProductionCommand is AsyncRelayCommand productionCommand)
        {
            productionCommand.RaiseCanExecuteChanged();
        }
    }
}
