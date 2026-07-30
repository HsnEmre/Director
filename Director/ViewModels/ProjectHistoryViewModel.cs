using System.Collections.ObjectModel;
using System.Windows.Input;
using Director.Commands;
using Director.Dtos;
using Director.Enums;
using Director.Helpers;
using Director.Services.Interfaces;

namespace Director.ViewModels;

public sealed class ProjectHistoryViewModel : ObservableObject
{
    private readonly IFilmProjectService _filmProjectService;
    private readonly INavigationService _navigationService;
    private readonly IMessageService _messageService;
    private string? _searchText;
    private FilmProjectStatus? _selectedStatus;
    private string? _selectedStoryGenre;
    private FilmProjectListItemDto? _selectedProject;
    private bool _isBusy;

    public ProjectHistoryViewModel(
        IFilmProjectService filmProjectService,
        INavigationService navigationService,
        IMessageService messageService)
    {
        _filmProjectService = filmProjectService;
        _navigationService = navigationService;
        _messageService = messageService;
        Projects = new ObservableCollection<FilmProjectListItemDto>();
        StatusOptions = Enum.GetValues<FilmProjectStatus>().Cast<FilmProjectStatus?>().Prepend(null).ToList();
        StoryGenreOptions = new ObservableCollection<string>();
        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        OpenCommand = new AsyncRelayCommand(OpenSelectedAsync, () => SelectedProject is not null && !IsBusy);
        EditSettingsCommand = new AsyncRelayCommand(EditSettingsAsync, () => SelectedProject is not null && !IsBusy);
        StoryCommand = new AsyncRelayCommand(GoStoryAsync, () => SelectedProject is not null && !IsBusy);
        DeleteCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => SelectedProject is not null && !IsBusy);
    }

    public ObservableCollection<FilmProjectListItemDto> Projects { get; }
    public ObservableCollection<string> StoryGenreOptions { get; }
    public IReadOnlyList<FilmProjectStatus?> StatusOptions { get; }

    public string? SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = LoadAsync();
            }
        }
    }

    public FilmProjectStatus? SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                _ = LoadAsync();
            }
        }
    }

    public string? SelectedStoryGenre
    {
        get => _selectedStoryGenre;
        set
        {
            if (SetProperty(ref _selectedStoryGenre, value))
            {
                _ = LoadAsync();
            }
        }
    }

    public FilmProjectListItemDto? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand EditSettingsCommand { get; }
    public ICommand StoryCommand { get; }
    public ICommand DeleteCommand { get; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var projects = await _filmProjectService.GetProjectHistoryAsync(SearchText, SelectedStatus, SelectedStoryGenre);
            Projects.Clear();
            foreach (var project in projects)
            {
                Projects.Add(project);
            }

            var genres = projects.Select(project => project.StoryGenre).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct().OrderBy(item => item).ToList();
            StoryGenreOptions.Clear();
            foreach (var genre in genres)
            {
                StoryGenreOptions.Add(genre);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private Task OpenSelectedAsync()
    {
        if (SelectedProject is null)
        {
            return Task.CompletedTask;
        }

        return SelectedProject.Status switch
        {
            FilmProjectStatus.Draft => _navigationService.NavigateToProjectSetupAsync(SelectedProject.Id),
            FilmProjectStatus.StoryGenerated or FilmProjectStatus.ProductionStarted or FilmProjectStatus.Completed => _navigationService.NavigateToStoryGenerationAsync(SelectedProject.Id),
            _ => _navigationService.NavigateToStoryGenerationAsync(SelectedProject.Id)
        };
    }

    private Task EditSettingsAsync() => SelectedProject is null
        ? Task.CompletedTask
        : _navigationService.NavigateToProjectSetupAsync(SelectedProject.Id);

    private Task GoStoryAsync() => SelectedProject is null
        ? Task.CompletedTask
        : _navigationService.NavigateToStoryGenerationAsync(SelectedProject.Id);

    private async Task DeleteSelectedAsync()
    {
        if (SelectedProject is null)
        {
            return;
        }

        if (!_messageService.Confirm($"{SelectedProject.ProjectName} silinecek. Devam edilsin mi?", "Projeyi Sil"))
        {
            return;
        }

        await _filmProjectService.DeleteAsync(SelectedProject.Id);
        await LoadAsync();
    }

    private void RaiseCommandStates()
    {
        if (OpenCommand is AsyncRelayCommand openCommand) openCommand.RaiseCanExecuteChanged();
        if (EditSettingsCommand is AsyncRelayCommand editCommand) editCommand.RaiseCanExecuteChanged();
        if (StoryCommand is AsyncRelayCommand storyCommand) storyCommand.RaiseCanExecuteChanged();
        if (DeleteCommand is AsyncRelayCommand deleteCommand) deleteCommand.RaiseCanExecuteChanged();
        if (RefreshCommand is AsyncRelayCommand refreshCommand) refreshCommand.RaiseCanExecuteChanged();
    }
}
