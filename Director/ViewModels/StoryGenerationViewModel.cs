using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Director.Commands;
using Director.Data;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Helpers;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Director.ViewModels;

public sealed class StoryGenerationViewModel : ObservableObject
{
    private const int MaxLogEntries = 500;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOllamaClient _ollamaClient;
    private readonly IStoryGenerationService _storyGenerationService;
    private readonly IMessageService _messageService;
    private readonly INavigationService _navigationService;
    private readonly OllamaOptions _options;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly Stopwatch _stopwatch = new();
    private CancellationTokenSource? _generationCancellation;
    private int _filmProjectId;
    private bool _isBusy;
    private bool _hasHealthChecked;
    private string _projectName = string.Empty;
    private string _projectSummary = string.Empty;
    private string _subjectPreview = string.Empty;
    private string _ollamaStatus = "Henüz test edilmedi";
    private string _phase = "Hazır";
    private string _progressMessage = "Üretim kullanıcı komutuyla başlayacak.";
    private int _completedItems;
    private int _totalItems;
    private double _percentage;
    private string _elapsedTimeText = "00:00";
    private string _storyTitle = string.Empty;
    private string _logline = string.Empty;
    private string _synopsis = string.Empty;
    private string _worldDescription = string.Empty;
    private string _visualDirection = string.Empty;
    private string _charactersSummary = string.Empty;
    private string _continuityRules = string.Empty;
    private StorySceneRowViewModel? _selectedScene;

    public StoryGenerationViewModel(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        IStoryGenerationService storyGenerationService,
        IMessageService messageService,
        INavigationService navigationService,
        IOptions<OllamaOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _ollamaClient = ollamaClient;
        _storyGenerationService = storyGenerationService;
        _messageService = messageService;
        _navigationService = navigationService;
        _options = options.Value;

        Scenes = new ObservableCollection<StorySceneRowViewModel>();
        GenerationLogs = new ObservableCollection<GenerationLogEntry>();
        TestOllamaCommand = new AsyncRelayCommand(TestOllamaAsync, () => !IsBusy);
        GenerateStoryCommand = new AsyncRelayCommand(GenerateStoryAsync, () => !IsBusy && FilmProjectId > 0);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        BackCommand = new AsyncRelayCommand(() => _navigationService.NavigateToProjectSetupAsync(FilmProjectId), () => !IsBusy);
        GoToProductionCommand = new AsyncRelayCommand(() => _navigationService.NavigateToProductionAsync(FilmProjectId), () => !IsBusy && FilmProjectId > 0);
        ProjectHistoryCommand = new AsyncRelayCommand(_navigationService.NavigateToProjectHistoryAsync, () => !IsBusy);
        ClearLogsCommand = new RelayCommand(() => GenerationLogs.Clear(), () => !IsBusy);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ElapsedTimeText = _stopwatch.Elapsed.ToString(@"mm\:ss");
    }

    public int FilmProjectId { get => _filmProjectId; internal set => SetProperty(ref _filmProjectId, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public bool HasHealthChecked { get => _hasHealthChecked; private set => SetProperty(ref _hasHealthChecked, value); }
    public string ProjectName { get => _projectName; private set => SetProperty(ref _projectName, value); }
    public string ProjectSummary { get => _projectSummary; private set => SetProperty(ref _projectSummary, value); }
    public string SubjectPreview { get => _subjectPreview; private set => SetProperty(ref _subjectPreview, value); }
    public string ModelName => _options.StoryTextModel;
    public string OllamaStatus { get => _ollamaStatus; private set => SetProperty(ref _ollamaStatus, value); }
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }
    public string ProgressMessage { get => _progressMessage; private set => SetProperty(ref _progressMessage, value); }
    public int CompletedItems { get => _completedItems; private set => SetProperty(ref _completedItems, value); }
    public int TotalItems { get => _totalItems; private set => SetProperty(ref _totalItems, value); }
    public double Percentage { get => _percentage; private set => SetProperty(ref _percentage, value); }
    public string ElapsedTimeText { get => _elapsedTimeText; private set => SetProperty(ref _elapsedTimeText, value); }
    public string StoryTitle { get => _storyTitle; private set => SetProperty(ref _storyTitle, value); }
    public string Logline { get => _logline; private set => SetProperty(ref _logline, value); }
    public string Synopsis { get => _synopsis; private set => SetProperty(ref _synopsis, value); }
    public string WorldDescription { get => _worldDescription; private set => SetProperty(ref _worldDescription, value); }
    public string VisualDirection { get => _visualDirection; private set => SetProperty(ref _visualDirection, value); }
    public string CharactersSummary { get => _charactersSummary; private set => SetProperty(ref _charactersSummary, value); }
    public string ContinuityRules { get => _continuityRules; private set => SetProperty(ref _continuityRules, value); }
    public StorySceneRowViewModel? SelectedScene { get => _selectedScene; set => SetProperty(ref _selectedScene, value); }

    public ObservableCollection<StorySceneRowViewModel> Scenes { get; }
    public ObservableCollection<GenerationLogEntry> GenerationLogs { get; }

    public ICommand TestOllamaCommand { get; }
    public ICommand GenerateStoryCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand GoToProductionCommand { get; }
    public ICommand ProjectHistoryCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand OpenLogFolderCommand { get; }

    public async Task InitializeAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        FilmProjectId = filmProjectId;
        await LoadProjectAsync(cancellationToken);
        await LoadGeneratedContentAsync(cancellationToken);
        RaiseCommandStates();
    }

    private async Task LoadProjectAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.FilmProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == FilmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadı.");

        ProjectName = project.ProjectName;
        SubjectPreview = project.Subject;
        ProjectSummary = $"{project.TotalDurationMinutes} dk | {project.ClipDurationSeconds} sn klip | {project.CalculatedClipCount} sahne | {project.Language} | {project.Resolution}";
        TotalItems = project.CalculatedClipCount;
    }

    private async Task TestOllamaAsync()
    {
        IsBusy = true;
        try
        {
            AddLog("Ollama kontrolü", "Ollama bağlantısı test ediliyor.", GenerationLogLevel.Information);
            OllamaStatus = "Ollama bağlantısı test ediliyor...";
            var health = await _ollamaClient.CheckHealthAsync();
            if (!health.IsAvailable)
            {
                OllamaStatus = health.Message;
                AddLog("Ollama kontrolü", health.Message, GenerationLogLevel.Error);
                _messageService.ShowError(health.Message);
                return;
            }

            await _ollamaClient.IsModelAvailableAsync(_options.StoryTextModel);
            HasHealthChecked = true;
            OllamaStatus = $"Bağlantı başarılı. Model kullanılabilir: {_options.StoryTextModel}";
            AddLog("Ollama kontrolü", OllamaStatus, GenerationLogLevel.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal async Task GenerateStoryAsync()
    {
        IsBusy = true;
        _generationCancellation = new CancellationTokenSource();
        _stopwatch.Restart();
        _elapsedTimer.Start();

        try
        {
            var progress = new Progress<StoryGenerationProgress>(OnProgressChanged);
            AddLog("Model", $"Model: {_options.StoryTextModel}", GenerationLogLevel.Information);
            await _storyGenerationService.GenerateAllMissingScenesAsync(FilmProjectId, progress, _generationCancellation.Token);
            await LoadGeneratedContentAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            Phase = "İptal edildi";
            ProgressMessage = "Hikaye üretimi kullanıcı tarafından iptal edildi.";
            AddLog("İptal", ProgressMessage, GenerationLogLevel.Warning);
        }
        catch (ProjectGenerationAlreadyRunningException)
        {
            Phase = "Proje üretimi kullanımda";
            ProgressMessage = ProjectGenerationAlreadyRunningException.UserMessage;
            AddLog("Proje üretimi", ProgressMessage, GenerationLogLevel.Warning);
            _messageService.ShowError(ProgressMessage);
        }
        catch (StoryCharacterValidationException ex)
        {
            Phase = "Karakter dogrulama hatasi";
            ProgressMessage = ex.Message;
            AddLog("Hata", $"{ex.Message} Teknik detay: {ex.TechnicalDetails}", GenerationLogLevel.Error);
            _messageService.ShowError(ex.Message);
        }
        catch (StorySceneGenerationException ex)
        {
            Phase = $"Sahne {ex.SceneNumber} durduruldu";
            ProgressMessage = $"Sahne {ex.SceneNumber} icin model cevabi dogrulanamadi. Onceki sahneler korundu.";
            AddLog("Sahne dogrulama", $"ProjectId={ex.FilmProjectId}; SceneNumber={ex.SceneNumber}; Stage={ex.Stage}; Teknik log={ex.LogPath}", GenerationLogLevel.Error, ex.SceneNumber, ex.SceneNumber);
            await LoadGeneratedContentAsync(CancellationToken.None);
            _messageService.ShowError(
                $"Sahne {ex.SceneNumber} için model cevabı doğrulanamadı. Önceki sahneleriniz korunmuştur. " +
                $"Aynı sahneyi yeniden deneyebilir veya daha sonra projeye devam edebilirsiniz. Teknik log: {ex.LogPath}");
        }
        catch (Exception ex)
        {
            AddLog("Hata", ex.Message, GenerationLogLevel.Error);
            throw;
        }
        finally
        {
            _elapsedTimer.Stop();
            _stopwatch.Stop();
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            IsBusy = false;
        }
    }

    private async Task LoadGeneratedContentAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await db.FilmStories
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.Characters)
            .FirstOrDefaultAsync(item => item.FilmProjectId == FilmProjectId, cancellationToken);

        StoryTitle = story?.Title ?? string.Empty;
        Logline = story?.Logline ?? string.Empty;
        Synopsis = story?.Synopsis ?? string.Empty;
        WorldDescription = story?.WorldDescription ?? string.Empty;
        VisualDirection = story?.VisualDirection ?? string.Empty;
        ContinuityRules = story?.ContinuityRulesJson ?? string.Empty;
        CharactersSummary = story is null
            ? string.Empty
            : string.Join(Environment.NewLine, story.Characters.OrderBy(item => item.SortOrder).Select(item => $"{item.Name} - {item.Role}: {item.ContinuityDescription}"));

        var scenes = await db.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == FilmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .Select(scene => new StorySceneRowViewModel
            {
                SceneNumber = scene.SceneNumber,
                Title = scene.Title,
                DurationSeconds = scene.DurationSeconds,
                StoryBeat = scene.StoryBeat,
                ImagePrompt = scene.ImagePrompt,
                ImageNegativePrompt = scene.ImageNegativePrompt,
                VideoPrompt = scene.VideoPrompt,
                VideoNegativePrompt = scene.VideoNegativePrompt,
                NarrationText = scene.NarrationText,
                DialogueJson = scene.DialogueJson,
                ContinuityFromPreviousScene = scene.ContinuityFromPreviousScene,
                ValidationChecklistJson = scene.ValidationChecklistJson,
                Status = scene.Status
            })
            .ToListAsync(cancellationToken);

        Scenes.Clear();
        foreach (var scene in scenes)
        {
            Scenes.Add(scene);
        }

        SelectedScene = Scenes.FirstOrDefault();
    }

    private void OnProgressChanged(StoryGenerationProgress progress)
    {
        Phase = progress.Phase;
        ProgressMessage = progress.Message;
        CompletedItems = progress.CompletedItems;
        if (progress.TotalItems > 0)
        {
            TotalItems = progress.TotalItems;
        }
        Percentage = progress.Percentage;
        AddLog(progress.Phase, progress.Message, progress.Level, progress.SceneStart, progress.SceneEnd, progress.Percentage);
    }

    private void AddLog(string phase, string message, GenerationLogLevel level, int? sceneStart = null, int? sceneEnd = null, double? percentage = null)
    {
        GenerationLogs.Add(new GenerationLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Phase = phase,
            Message = message,
            SceneStart = sceneStart,
            SceneEnd = sceneEnd,
            Percentage = percentage
        });

        while (GenerationLogs.Count > MaxLogEntries)
        {
            GenerationLogs.RemoveAt(0);
        }
    }

    private void Cancel() => _generationCancellation?.Cancel();

    private static void OpenLogFolder()
    {
        var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDir);
        Process.Start(new ProcessStartInfo { FileName = logDir, UseShellExecute = true });
    }

    private void RaiseCommandStates()
    {
        if (TestOllamaCommand is AsyncRelayCommand testCommand) testCommand.RaiseCanExecuteChanged();
        if (GenerateStoryCommand is AsyncRelayCommand generateCommand) generateCommand.RaiseCanExecuteChanged();
        if (CancelCommand is RelayCommand cancelCommand) cancelCommand.RaiseCanExecuteChanged();
        if (BackCommand is AsyncRelayCommand backCommand) backCommand.RaiseCanExecuteChanged();
        if (GoToProductionCommand is AsyncRelayCommand productionCommand) productionCommand.RaiseCanExecuteChanged();
        if (ProjectHistoryCommand is AsyncRelayCommand historyCommand) historyCommand.RaiseCanExecuteChanged();
        if (ClearLogsCommand is RelayCommand clearLogsCommand) clearLogsCommand.RaiseCanExecuteChanged();
    }
}
