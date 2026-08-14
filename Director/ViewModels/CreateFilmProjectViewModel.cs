using System.Windows.Input;
using Director.Commands;
using Director.Dtos.Autonomous;
using Director.Enums;
using Director.Helpers;
using Director.Models;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.ViewModels;

public class CreateFilmProjectViewModel : ValidatableObservableObject
{
    private readonly IFilmProjectService _filmProjectService;
    private readonly IAutonomousGenerationRunService _autonomousRunService;
    private readonly IMessageService _messageService;
    private readonly INavigationService _navigationService;
    private readonly IVideoModelCapabilityService _videoModelCapabilityService;
    private readonly OllamaOptions _ollamaOptions;
    private bool _isInitializing;
    private bool _hasUnsavedChanges;

    private int? _currentProjectId;
    private string _projectName = string.Empty;
    private string _subject = string.Empty;
    private int _totalDurationMinutes = 20;
    private bool _useSecondBasedTargetDuration;
    private int _targetDurationSeconds = 10;
    private int _clipDurationSeconds = 10;
    private int _calculatedClipCount = 120;
    private string _calculatedOutputDurationText = string.Empty;
    private string _durationWarningText = string.Empty;
    private string _language = "Türkçe";
    private string _targetAudience = "Genel İzleyici";
    private string _storyGenre = string.Empty;
    private string _visualStyle = string.Empty;
    private string _videoStyle = string.Empty;
    private string _aspectRatio = "16:9";
    private string _resolution = "1920x1080";
    private bool _useNarrator;
    private bool _preferLtxNativeDialogue;
    private string? _narratorTone;
    private string? _mainCharacterDescription;
    private string? _additionalInstructions;
    private bool _isBusy;
    private string _statusMessage = "Yeni bir film projesi taslağı hazırlayın.";
    private string _aspectRatioWarningText = string.Empty;
    private bool _isAutonomousMode;
    private bool _hasAutonomousRun;
    private bool _hasActiveAutonomousRun;
    private int? _autonomousRunId;
    private double _autonomousProgressPercentage;
    private string _autonomousStatusText = "Otonom üretim henüz başlatılmadı.";

    public CreateFilmProjectViewModel(
        IFilmProjectService filmProjectService,
        IAutonomousGenerationRunService autonomousRunService,
        IMessageService messageService,
        INavigationService navigationService,
        IVideoModelCapabilityService videoModelCapabilityService,
        IOptions<OllamaOptions> ollamaOptions)
    {
        _filmProjectService = filmProjectService;
        _autonomousRunService = autonomousRunService;
        _messageService = messageService;
        _navigationService = navigationService;
        _videoModelCapabilityService = videoModelCapabilityService;
        _ollamaOptions = ollamaOptions.Value;

        ClipDurationOptions = _videoModelCapabilityService
            .GetCapability(VideoModelCapabilityService.VerifiedLtxModelType)
            .SupportedDurationsSeconds
            .OrderBy(duration => duration)
            .ToList();
        LanguageOptions = new List<string> { "Türkçe", "İngilizce", "Almanca", "Fransızca", "İspanyolca" };
        TargetAudienceOptions = new List<string> { "Çocuk", "Genç", "Yetişkin", "Aile", "Genel İzleyici" };
        StoryGenreOptions = new List<string> { "Macera", "Fantastik", "Bilim Kurgu", "Dram", "Komedi", "Korku", "Gerilim", "Belgesel", "Eğitici", "Masal" };
        VisualStyleOptions = new List<string> { "Sinematik Gerçekçi", "3D Animasyon", "2D Animasyon", "Anime", "Masal Kitabı İllüstrasyonu", "Stop Motion", "Karanlık Fantastik", "Belgesel Gerçekçiliği" };
        VideoStyleOptions = new List<string> { "Sinematik", "Yavaş ve Atmosferik", "Dinamik", "Belgesel", "Çocuk Animasyonu", "Reklam Filmi", "Müzik Videosu" };
        AspectRatioOptions = new List<string> { "16:9", "9:16", "1:1", "4:3", "21:9" };
        ResolutionOptions = new List<string> { "1280x720", "1920x1080", "1080x1920", "1024x1024" };
        NarratorToneOptions = new List<string> { "Sakin ve sıcak", "Masalsı", "Dramatik", "Belgesel anlatımı", "Enerjik", "Gizemli" };

        SaveDraftCommand = new AsyncRelayCommand(() => SaveAsync(FilmProjectStatus.Draft), () => !IsBusy);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync, () => !IsBusy && (!IsAutonomousMode || !HasActiveAutonomousRun));
        ClearFormCommand = new RelayCommand(ClearForm, () => !IsBusy);
        PauseAutonomousCommand = new AsyncRelayCommand(PauseAutonomousAsync, () => !IsBusy && AutonomousRunId is int);
        ResumeAutonomousCommand = new AsyncRelayCommand(ResumeAutonomousAsync, () => !IsBusy && AutonomousRunId is int);
        CancelAutonomousCommand = new AsyncRelayCommand(CancelAutonomousAsync, () => !IsBusy && AutonomousRunId is int);
        RetryAutonomousCommand = new AsyncRelayCommand(RetryAutonomousAsync, () => !IsBusy && AutonomousRunId is int);

        RecalculateClipCount();
        ValidateAll();
        _hasUnsavedChanges = false;
    }

    public int? CurrentProjectId
    {
        get => _currentProjectId;
        set => SetProperty(ref _currentProjectId, value);
    }

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, value))
            {
                ValidateProjectName();
                MarkDirty();
            }
        }
    }

    public string Subject
    {
        get => _subject;
        set
        {
            if (SetProperty(ref _subject, value))
            {
                ValidateSubject();
                MarkDirty();
            }
        }
    }

    public int TotalDurationMinutes
    {
        get => _totalDurationMinutes;
        set
        {
            if (SetProperty(ref _totalDurationMinutes, value))
            {
                RecalculateClipCount();
                ValidateTotalDurationMinutes();
                MarkDirty();
            }
        }
    }

    public bool UseSecondBasedTargetDuration
    {
        get => _useSecondBasedTargetDuration;
        set
        {
            if (SetProperty(ref _useSecondBasedTargetDuration, value))
            {
                RecalculateClipCount();
                ValidateTotalDurationMinutes();
                ValidateTargetDurationSeconds();
                OnPropertyChanged(nameof(IsMinuteBasedTargetDuration));
                MarkDirty();
            }
        }
    }

    public bool IsMinuteBasedTargetDuration => !UseSecondBasedTargetDuration;

    public int TargetDurationSeconds
    {
        get => _targetDurationSeconds;
        set
        {
            if (SetProperty(ref _targetDurationSeconds, value))
            {
                RecalculateClipCount();
                ValidateTargetDurationSeconds();
                MarkDirty();
            }
        }
    }

    public int ClipDurationSeconds
    {
        get => _clipDurationSeconds;
        set
        {
            if (SetProperty(ref _clipDurationSeconds, value))
            {
                RecalculateClipCount();
                ValidateClipDurationSeconds();
                MarkDirty();
            }
        }
    }

    public int CalculatedClipCount
    {
        get => _calculatedClipCount;
        private set => SetProperty(ref _calculatedClipCount, value);
    }

    public string CalculatedOutputDurationText
    {
        get => _calculatedOutputDurationText;
        private set => SetProperty(ref _calculatedOutputDurationText, value);
    }

    public string DurationWarningText
    {
        get => _durationWarningText;
        private set => SetProperty(ref _durationWarningText, value);
    }

    public string Language
    {
        get => _language;
        set
        {
            if (SetProperty(ref _language, value))
            {
                ValidateRequired(nameof(Language), Language, "Dil zorunludur.");
                MarkDirty();
            }
        }
    }

    public string TargetAudience
    {
        get => _targetAudience;
        set
        {
            if (SetProperty(ref _targetAudience, value))
            {
                MarkDirty();
            }
        }
    }

    public string StoryGenre
    {
        get => _storyGenre;
        set
        {
            if (SetProperty(ref _storyGenre, value))
            {
                ValidateRequired(nameof(StoryGenre), StoryGenre, "Hikâye türü zorunludur.");
                MarkDirty();
            }
        }
    }

    public string VisualStyle
    {
        get => _visualStyle;
        set
        {
            if (SetProperty(ref _visualStyle, value))
            {
                ValidateRequired(nameof(VisualStyle), VisualStyle, "Görsel stil zorunludur.");
                MarkDirty();
            }
        }
    }

    public string VideoStyle
    {
        get => _videoStyle;
        set
        {
            if (SetProperty(ref _videoStyle, value))
            {
                ValidateRequired(nameof(VideoStyle), VideoStyle, "Video stili zorunludur.");
                MarkDirty();
            }
        }
    }

    public string AspectRatio
    {
        get => _aspectRatio;
        set
        {
            if (SetProperty(ref _aspectRatio, value))
            {
                ValidateRequired(nameof(AspectRatio), AspectRatio, "En-boy oranı zorunludur.");
                ValidateResolutionMatch();
                MarkDirty();
            }
        }
    }

    public string Resolution
    {
        get => _resolution;
        set
        {
            if (SetProperty(ref _resolution, value))
            {
                ValidateRequired(nameof(Resolution), Resolution, "Çözünürlük zorunludur.");
                ValidateResolutionMatch();
                MarkDirty();
            }
        }
    }

    public bool UseNarrator
    {
        get => _useNarrator;
        set
        {
            if (SetProperty(ref _useNarrator, value))
            {
                ValidateNarratorTone();
                MarkDirty();
            }
        }
    }

    public bool PreferLtxNativeDialogue
    {
        get => _preferLtxNativeDialogue;
        set
        {
            if (SetProperty(ref _preferLtxNativeDialogue, value))
            {
                MarkDirty();
            }
        }
    }

    public string? NarratorTone
    {
        get => _narratorTone;
        set
        {
            if (SetProperty(ref _narratorTone, value))
            {
                ValidateNarratorTone();
                MarkDirty();
            }
        }
    }

    public string? MainCharacterDescription
    {
        get => _mainCharacterDescription;
        set
        {
            if (SetProperty(ref _mainCharacterDescription, value))
            {
                MarkDirty();
            }
        }
    }

    public string? AdditionalInstructions
    {
        get => _additionalInstructions;
        set
        {
            if (SetProperty(ref _additionalInstructions, value))
            {
                MarkDirty();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string AspectRatioWarningText
    {
        get => _aspectRatioWarningText;
        private set => SetProperty(ref _aspectRatioWarningText, value);
    }

    public bool IsAutonomousMode
    {
        get => _isAutonomousMode;
        set
        {
            if (SetProperty(ref _isAutonomousMode, value))
            {
                OnPropertyChanged(nameof(PrimaryActionText));
                RaiseCommandStates();
                MarkDirty();
            }
        }
    }

    public string PrimaryActionText => IsAutonomousMode
        ? "Otonom Üretimi Başlat / Sürdür"
        : "Hikayeyi Hazırlamaya Devam Et";

    public bool HasAutonomousRun
    {
        get => _hasAutonomousRun;
        private set
        {
            if (SetProperty(ref _hasAutonomousRun, value))
            {
                OnPropertyChanged(nameof(PrimaryActionText));
            }
        }
    }

    public int? AutonomousRunId
    {
        get => _autonomousRunId;
        private set
        {
            if (SetProperty(ref _autonomousRunId, value))
            {
                HasAutonomousRun = value is not null;
                RaiseCommandStates();
            }
        }
    }

    public bool HasActiveAutonomousRun
    {
        get => _hasActiveAutonomousRun;
        private set
        {
            if (SetProperty(ref _hasActiveAutonomousRun, value))
            {
                OnPropertyChanged(nameof(PrimaryActionText));
                RaiseCommandStates();
            }
        }
    }

    public double AutonomousProgressPercentage
    {
        get => _autonomousProgressPercentage;
        private set => SetProperty(ref _autonomousProgressPercentage, value);
    }

    public string AutonomousStatusText
    {
        get => _autonomousStatusText;
        private set => SetProperty(ref _autonomousStatusText, value);
    }

    public string ProjectNameError => GetFirstError(nameof(ProjectName));
    public string SubjectError => GetFirstError(nameof(Subject));
    public string TotalDurationMinutesError => GetFirstError(nameof(TotalDurationMinutes));
    public string TargetDurationSecondsError => GetFirstError(nameof(TargetDurationSeconds));
    public string ClipDurationSecondsError => GetFirstError(nameof(ClipDurationSeconds));
    public string LanguageError => GetFirstError(nameof(Language));
    public string StoryGenreError => GetFirstError(nameof(StoryGenre));
    public string VisualStyleError => GetFirstError(nameof(VisualStyle));
    public string VideoStyleError => GetFirstError(nameof(VideoStyle));
    public string AspectRatioError => GetFirstError(nameof(AspectRatio));
    public string ResolutionError => GetFirstError(nameof(Resolution));
    public string NarratorToneError => GetFirstError(nameof(NarratorTone));

    public IReadOnlyList<int> ClipDurationOptions { get; }
    public IReadOnlyList<string> LanguageOptions { get; }
    public IReadOnlyList<string> TargetAudienceOptions { get; }
    public IReadOnlyList<string> StoryGenreOptions { get; }
    public IReadOnlyList<string> VisualStyleOptions { get; }
    public IReadOnlyList<string> VideoStyleOptions { get; }
    public IReadOnlyList<string> AspectRatioOptions { get; }
    public IReadOnlyList<string> ResolutionOptions { get; }
    public IReadOnlyList<string> NarratorToneOptions { get; }

    public ICommand SaveDraftCommand { get; }
    public ICommand ContinueCommand { get; }
    public ICommand ClearFormCommand { get; }
    public ICommand PauseAutonomousCommand { get; }
    public ICommand ResumeAutonomousCommand { get; }
    public ICommand CancelAutonomousCommand { get; }
    public ICommand RetryAutonomousCommand { get; }

    public async Task LoadProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await _filmProjectService.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadı.");

        _isInitializing = true;
        CurrentProjectId = project.Id;
        ProjectName = project.ProjectName;
        Subject = project.Subject;
        UseSecondBasedTargetDuration = false;
        TargetDurationSeconds = Math.Max(
            project.ClipDurationSeconds,
            FilmDurationPlanner.CalculateOutputDurationSeconds(project.CalculatedClipCount, project.ClipDurationSeconds));
        TotalDurationMinutes = project.TotalDurationMinutes;
        ClipDurationSeconds = project.ClipDurationSeconds;
        Language = project.Language;
        TargetAudience = project.TargetAudience;
        StoryGenre = project.StoryGenre;
        VisualStyle = project.VisualStyle;
        VideoStyle = project.VideoStyle;
        AspectRatio = project.AspectRatio;
        Resolution = project.Resolution;
        UseNarrator = project.UseNarrator;
        PreferLtxNativeDialogue = false;
        IsAutonomousMode = project.AutonomousModeEnabled;
        NarratorTone = project.NarratorTone;
        MainCharacterDescription = project.MainCharacterDescription;
        AdditionalInstructions = project.AdditionalInstructions;
        StatusMessage = "Kayıtlı proje ayarları yüklendi.";
        EnsureSupportedClipDurationForDefaultVideoModel();
        _isInitializing = false;
        _hasUnsavedChanges = false;
        ValidateAll();
        await LoadAutonomousRunSummaryAsync(cancellationToken);
    }

    private Task ContinueAsync() => IsAutonomousMode
        ? SaveAndStartAutonomousAsync()
        : SaveAsync(FilmProjectStatus.ReadyForStoryGeneration);

    private async Task SaveAsync(FilmProjectStatus status)
    {
        ValidateAll();
        if (HasErrors)
        {
            StatusMessage = "Lütfen işaretlenen alanları düzeltin.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Proje ayarları kaydediliyor...";

        try
        {
            if (CurrentProjectId is int projectId)
            {
                var existingProject = await _filmProjectService.GetByIdAsync(projectId);
                if (existingProject is null)
                {
                    CurrentProjectId = null;
                    var recreatedProject = BuildProject(status);
                    var createdProject = await _filmProjectService.CreateAsync(recreatedProject);
                    CurrentProjectId = createdProject.Id;
                }
                else
                {
                    ApplyToProject(existingProject, status);
                    await _filmProjectService.UpdateAsync(existingProject);
                }
            }
            else
            {
                var project = BuildProject(status);
                var createdProject = await _filmProjectService.CreateAsync(project);
                CurrentProjectId = createdProject.Id;
            }

            _hasUnsavedChanges = false;
            StatusMessage = status == FilmProjectStatus.Draft
                ? "Taslak kaydedildi."
                : "Proje ayarları kaydedildi. Hikâye üretimi için hazır.";

            if (status == FilmProjectStatus.ReadyForStoryGeneration)
            {
                _messageService.ShowInfo("Proje ayarları kaydedildi. Hikâye üretimi için hazır.");
                if (CurrentProjectId is int savedProjectId)
                {
                    await _navigationService.NavigateToStoryGenerationAsync(savedProjectId);
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAndStartAutonomousAsync()
    {
        ValidateAll();
        if (HasErrors)
        {
            StatusMessage = "Lütfen işaretlenen alanları düzeltin.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Otonom üretim için proje ayarları kaydediliyor...";

        try
        {
            if (CurrentProjectId is int projectId)
            {
                var existingProject = await _filmProjectService.GetByIdAsync(projectId);
                if (existingProject is null)
                {
                    CurrentProjectId = null;
                    var recreatedProject = BuildProject(FilmProjectStatus.ReadyForStoryGeneration);
                    var createdProject = await _filmProjectService.CreateAsync(recreatedProject);
                    CurrentProjectId = createdProject.Id;
                }
                else
                {
                    ApplyToProject(existingProject, FilmProjectStatus.ReadyForStoryGeneration);
                    await _filmProjectService.UpdateAsync(existingProject);
                }
            }
            else
            {
                var project = BuildProject(FilmProjectStatus.ReadyForStoryGeneration);
                var createdProject = await _filmProjectService.CreateAsync(project);
                CurrentProjectId = createdProject.Id;
            }

            if (CurrentProjectId is not int savedProjectId)
            {
                throw new InvalidOperationException("Otonom üretim için proje kaydı oluşturulamadı.");
            }

            var summary = await _autonomousRunService.StartOrGetActiveRunAsync(savedProjectId, BuildAutonomousSnapshot(savedProjectId));
            ApplyAutonomousSummary(summary);
            _hasUnsavedChanges = false;
            StatusMessage = "Otonom üretim kuyruğa alındı. Arka plan worker güvenli checkpoint'lerden devam edecek.";
            _messageService.ShowInfo("Otonom üretim başlatıldı/sürdürüldü.");
            await NavigateToAutonomousWorkspaceAsync(summary);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PauseAutonomousAsync()
    {
        if (AutonomousRunId is int runId)
        {
            await _autonomousRunService.PauseAsync(runId);
            await LoadAutonomousRunSummaryAsync();
        }
    }

    private async Task ResumeAutonomousAsync()
    {
        if (CurrentProjectId is not int projectId)
        {
            return;
        }

        var latest = await _autonomousRunService.GetLatestRunForProjectAsync(projectId);
        if (latest?.Status == AutonomousGenerationRunStatus.Paused)
        {
            await _autonomousRunService.ResumeAsync(latest.Id);
            await LoadAutonomousRunSummaryAsync();
            var resumed = await _autonomousRunService.GetLatestRunForProjectAsync(projectId);
            if (resumed is not null)
            {
                await NavigateToAutonomousWorkspaceAsync(resumed);
            }
            return;
        }

        var summary = await _autonomousRunService.StartOrGetActiveRunAsync(projectId, BuildAutonomousSnapshot(projectId));
        ApplyAutonomousSummary(summary);
        StatusMessage = "Otonom üretim devam etmek üzere kuyruğa alındı.";
        await NavigateToAutonomousWorkspaceAsync(summary);
    }

    private async Task RetryAutonomousAsync()
    {
        if (CurrentProjectId is not int projectId)
        {
            return;
        }

        if (AutonomousRunId is int runId)
        {
            await _autonomousRunService.RetryAsync(runId);
        }

        var summary = await _autonomousRunService.StartOrGetActiveRunAsync(projectId, BuildAutonomousSnapshot(projectId));
        ApplyAutonomousSummary(summary);
        StatusMessage = "Otonom üretim retry/devam için kuyruğa alındı.";
        await NavigateToAutonomousWorkspaceAsync(summary);
    }

    private async Task CancelAutonomousAsync()
    {
        if (AutonomousRunId is int runId)
        {
            await _autonomousRunService.RequestCancellationAsync(runId);
            await LoadAutonomousRunSummaryAsync();
        }
    }

    private FilmProject BuildProject(FilmProjectStatus status)
    {
        var project = new FilmProject();
        ApplyToProject(project, status);
        return project;
    }

    private void ApplyToProject(FilmProject project, FilmProjectStatus status)
    {
        project.ProjectName = ProjectName.Trim();
        project.Subject = Subject.Trim();
        project.TotalDurationMinutes = UseSecondBasedTargetDuration
            ? Math.Max(1, (int)Math.Ceiling(GetTargetDurationSeconds() / 60.0))
            : TotalDurationMinutes;
        project.ClipDurationSeconds = ClipDurationSeconds;
        project.CalculatedClipCount = CalculatedClipCount;
        project.Language = Language.Trim();
        project.TargetAudience = TargetAudience.Trim();
        project.StoryGenre = StoryGenre.Trim();
        project.VisualStyle = VisualStyle.Trim();
        project.VideoStyle = VideoStyle.Trim();
        project.AspectRatio = AspectRatio.Trim();
        project.Resolution = Resolution.Trim();
        project.UseNarrator = UseNarrator;
        project.AutonomousModeEnabled = IsAutonomousMode;
        project.NarratorTone = UseNarrator ? NarratorTone?.Trim() : null;
        project.MainCharacterDescription = MainCharacterDescription?.Trim();
        project.AdditionalInstructions = AdditionalInstructions?.Trim();
        project.Status = status;
    }

    private void ClearForm()
    {
        if (_hasUnsavedChanges && !_messageService.Confirm("Kaydedilmemiş değişiklikler temizlenecek. Devam edilsin mi?", "Formu Temizle"))
        {
            return;
        }

        _isInitializing = true;

        CurrentProjectId = null;
        ProjectName = string.Empty;
        Subject = string.Empty;
        UseSecondBasedTargetDuration = false;
        TotalDurationMinutes = 20;
        TargetDurationSeconds = 10;
        ClipDurationSeconds = 10;
        Language = "Türkçe";
        TargetAudience = "Genel İzleyici";
        StoryGenre = string.Empty;
        VisualStyle = string.Empty;
        VideoStyle = string.Empty;
        AspectRatio = "16:9";
        Resolution = "1920x1080";
        UseNarrator = false;
        PreferLtxNativeDialogue = false;
        NarratorTone = null;
        MainCharacterDescription = null;
        AdditionalInstructions = null;
        StatusMessage = "Form varsayılan değerlere döndürüldü.";

        _isInitializing = false;
        _hasUnsavedChanges = false;
        ValidateAll();
    }

    private void RecalculateClipCount()
    {
        var totalSeconds = GetTargetDurationSeconds();
        if (totalSeconds <= 0 || ClipDurationSeconds <= 0)
        {
            CalculatedClipCount = 0;
            CalculatedOutputDurationText = "Hesaplanamadı";
            DurationWarningText = string.Empty;
            return;
        }

        CalculatedClipCount = FilmDurationPlanner.CalculateClipCountForTargetSeconds(totalSeconds, ClipDurationSeconds);
        var outputSeconds = FilmDurationPlanner.CalculateOutputDurationSeconds(CalculatedClipCount, ClipDurationSeconds);
        CalculatedOutputDurationText = $"{CalculatedClipCount} klip × {ClipDurationSeconds} sn = {outputSeconds} sn";
        DurationWarningText = outputSeconds > totalSeconds
            ? $"Son klip nedeniyle hedef süreden {outputSeconds - totalSeconds} saniye fazla üretim planlanıyor."
            : string.Empty;
    }

    private void ValidateAll()
    {
        ValidateProjectName();
        ValidateSubject();
        ValidateTotalDurationMinutes();
        ValidateTargetDurationSeconds();
        ValidateClipDurationSeconds();
        ValidateRequired(nameof(Language), Language, "Dil zorunludur.");
        ValidateRequired(nameof(StoryGenre), StoryGenre, "Hikâye türü zorunludur.");
        ValidateRequired(nameof(VisualStyle), VisualStyle, "Görsel stil zorunludur.");
        ValidateRequired(nameof(VideoStyle), VideoStyle, "Video stili zorunludur.");
        ValidateRequired(nameof(AspectRatio), AspectRatio, "En-boy oranı zorunludur.");
        ValidateRequired(nameof(Resolution), Resolution, "Çözünürlük zorunludur.");
        ValidateNarratorTone();
        ValidateResolutionMatch();
    }

    private void ValidateProjectName()
    {
        ValidateRequired(nameof(ProjectName), ProjectName, "Proje adı zorunludur.");
    }

    private void ValidateSubject()
    {
        ValidateRequired(nameof(Subject), Subject, "Film / video konusu zorunludur.");
    }

    private void ValidateTotalDurationMinutes()
    {
        var errors = new List<string>();
        if (!UseSecondBasedTargetDuration && (TotalDurationMinutes < 1 || TotalDurationMinutes > 180))
        {
            errors.Add("Toplam süre 1 ile 180 dakika arasında olmalıdır.");
        }

        SetErrors(nameof(TotalDurationMinutes), errors);
        OnPropertyChanged(nameof(TotalDurationMinutesError));
    }

    private void ValidateTargetDurationSeconds()
    {
        var errors = new List<string>();
        if (UseSecondBasedTargetDuration && (TargetDurationSeconds < 1 || TargetDurationSeconds > 10800))
        {
            errors.Add("Hedef sure 1 ile 10800 saniye arasinda olmalidir.");
        }

        SetErrors(nameof(TargetDurationSeconds), errors);
        OnPropertyChanged(nameof(TargetDurationSecondsError));
    }

    private void ValidateClipDurationSeconds()
    {
        var validation = _videoModelCapabilityService.ValidateDuration(VideoModelCapabilityService.VerifiedLtxModelType, ClipDurationSeconds);
        var errors = validation.IsValid
            ? Enumerable.Empty<string>()
            : new[] { validation.ErrorMessage };

        SetErrors(nameof(ClipDurationSeconds), errors);
        OnPropertyChanged(nameof(ClipDurationSecondsError));
    }

    private void EnsureSupportedClipDurationForDefaultVideoModel()
    {
        var validation = _videoModelCapabilityService.ValidateDuration(VideoModelCapabilityService.VerifiedLtxModelType, ClipDurationSeconds);
        if (validation.IsValid)
        {
            return;
        }

        ClipDurationSeconds = validation.Capability.DefaultDurationSeconds;
        StatusMessage = validation.ErrorMessage + " Varsayılan süreye dönüldü.";
    }

    private void ValidateNarratorTone()
    {
        var errors = UseNarrator && string.IsNullOrWhiteSpace(NarratorTone)
            ? new[] { "Anlatıcı açıksa anlatıcı tonu zorunludur." }
            : Enumerable.Empty<string>();

        SetErrors(nameof(NarratorTone), errors);
        OnPropertyChanged(nameof(NarratorToneError));
    }

    private void ValidateRequired(string propertyName, string? value, string message)
    {
        var errors = string.IsNullOrWhiteSpace(value)
            ? new[] { message }
            : Enumerable.Empty<string>();

        SetErrors(propertyName, errors);
        OnPropertyChanged(propertyName + "Error");
    }

    private void ValidateResolutionMatch()
    {
        AspectRatioWarningText = (AspectRatio, Resolution) switch
        {
            ("16:9", "1280x720" or "1920x1080") => string.Empty,
            ("9:16", "1080x1920") => string.Empty,
            ("1:1", "1024x1024") => string.Empty,
            ("4:3", _) => "Seçilen çözünürlük 4:3 oranıyla tam uyumlu değil. İlk sürümde kaydetme engellenmez.",
            ("21:9", _) => "Seçilen çözünürlük 21:9 oranıyla tam uyumlu değil. İlk sürümde kaydetme engellenmez.",
            _ => "En-boy oranı ve çözünürlük uyumsuz görünüyor. İlk sürümde kaydetme engellenmez."
        };
    }

    private void MarkDirty()
    {
        if (!_isInitializing)
        {
            _hasUnsavedChanges = true;
        }
    }

    private void RaiseCommandStates()
    {
        if (SaveDraftCommand is AsyncRelayCommand saveDraftCommand)
        {
            saveDraftCommand.RaiseCanExecuteChanged();
        }

        if (ContinueCommand is AsyncRelayCommand continueCommand)
        {
            continueCommand.RaiseCanExecuteChanged();
        }

        if (ClearFormCommand is RelayCommand clearFormCommand)
        {
            clearFormCommand.RaiseCanExecuteChanged();
        }

        if (PauseAutonomousCommand is AsyncRelayCommand pauseCommand)
        {
            pauseCommand.RaiseCanExecuteChanged();
        }

        if (ResumeAutonomousCommand is AsyncRelayCommand resumeCommand)
        {
            resumeCommand.RaiseCanExecuteChanged();
        }

        if (CancelAutonomousCommand is AsyncRelayCommand cancelCommand)
        {
            cancelCommand.RaiseCanExecuteChanged();
        }

        if (RetryAutonomousCommand is AsyncRelayCommand retryCommand)
        {
            retryCommand.RaiseCanExecuteChanged();
        }
    }

    private AutonomousGenerationConfigurationSnapshot BuildAutonomousSnapshot(int filmProjectId) => new()
    {
        FilmProjectId = filmProjectId,
        ProjectName = ProjectName.Trim(),
        Subject = Subject.Trim(),
        TargetDurationSeconds = GetTargetDurationSeconds(),
        TotalDurationMinutes = UseSecondBasedTargetDuration
            ? Math.Max(1, (int)Math.Ceiling(GetTargetDurationSeconds() / 60.0))
            : TotalDurationMinutes,
        ClipDurationSeconds = ClipDurationSeconds,
        CalculatedClipCount = CalculatedClipCount,
        Language = Language.Trim(),
        TargetAudience = TargetAudience.Trim(),
        StoryGenre = StoryGenre.Trim(),
        VisualStyle = VisualStyle.Trim(),
        VideoStyle = VideoStyle.Trim(),
        AspectRatio = AspectRatio.Trim(),
        Resolution = Resolution.Trim(),
        UseNarrator = UseNarrator,
        NarratorTone = UseNarrator ? NarratorTone?.Trim() : null,
        MainCharacterDescription = MainCharacterDescription?.Trim(),
        AdditionalInstructions = AdditionalInstructions?.Trim(),
        StoryModel = _ollamaOptions.StoryTextModel,
        ImageModelType = "qwen_image_20B",
        VideoModelType = "ltx2_22B_distilled_gguf_q4_k_m",
        ImageInferenceSteps = 20,
        VideoInferenceSteps = 12,
        RandomSeed = true,
        GenerateAudio = UseNarrator,
        PreferLtxNativeDialogue = PreferLtxNativeDialogue
    };

    private int GetTargetDurationSeconds() =>
        UseSecondBasedTargetDuration ? TargetDurationSeconds : TotalDurationMinutes * 60;

    private async Task LoadAutonomousRunSummaryAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentProjectId is not int projectId)
        {
            return;
        }

        try
        {
            var summary = await _autonomousRunService.GetLatestRunForProjectAsync(projectId, cancellationToken);
            if (summary is not null)
            {
                ApplyAutonomousSummary(summary);
            }
        }
        catch
        {
            AutonomousStatusText = "Otonom durum okunamadı. Migration henüz uygulanmadıysa bu beklenen bir durumdur.";
        }
    }

    private void ApplyAutonomousSummary(AutonomousGenerationRunSummary summary)
    {
        AutonomousRunId = summary.Id;
        HasActiveAutonomousRun = summary.IsActive && summary.Status != AutonomousGenerationRunStatus.Paused;
        AutonomousProgressPercentage = summary.OverallProgressPercentage;
        var sceneText = summary.CurrentSceneNumber is int sceneNumber ? $" Sahne: {sceneNumber}." : string.Empty;
        AutonomousStatusText = $"{summary.Status} / {summary.CurrentStage}. İlerleme: {summary.OverallProgressPercentage:0.#}%.{sceneText} {summary.LastMessage}".Trim();
    }

    private async Task NavigateToAutonomousWorkspaceAsync(AutonomousGenerationRunSummary summary)
    {
        var stage = ResolveStageFromSummary(summary);
        if (stage is AutonomousGenerationStage.Pending or AutonomousGenerationStage.Validating or AutonomousGenerationStage.Failed)
        {
            var checkpoint = await _autonomousRunService.GetProjectCheckpointAsync(summary.FilmProjectId);
            stage = ResolveStageFromCheckpoint(checkpoint);
        }

        if (IsStoryWorkspaceStage(stage))
        {
            await _navigationService.NavigateToStoryGenerationAsync(summary.FilmProjectId);
            return;
        }

        var tabIndex = stage == AutonomousGenerationStage.GeneratingImages ? 0 : 1;
        await _navigationService.NavigateToProductionAsync(summary.FilmProjectId, tabIndex);
    }

    private static AutonomousGenerationStage ResolveStageFromSummary(AutonomousGenerationRunSummary summary)
    {
        if (summary.CurrentStage != AutonomousGenerationStage.Pending)
        {
            return summary.CurrentStage;
        }

        return summary.Status switch
        {
            AutonomousGenerationRunStatus.GeneratingStory or
            AutonomousGenerationRunStatus.GeneratingScenes or
            AutonomousGenerationRunStatus.GeneratingStoryNarrative or
            AutonomousGenerationRunStatus.GeneratingCharacters or
            AutonomousGenerationRunStatus.GeneratingNarrativeScenes or
            AutonomousGenerationRunStatus.GeneratingImagePrompts or
            AutonomousGenerationRunStatus.GeneratingVideoPrompts => AutonomousGenerationStage.GeneratingVideoPrompts,
            AutonomousGenerationRunStatus.GeneratingImages => AutonomousGenerationStage.GeneratingImages,
            AutonomousGenerationRunStatus.GeneratingVideos => AutonomousGenerationStage.GeneratingVideos,
            AutonomousGenerationRunStatus.GeneratingAudio => AutonomousGenerationStage.GeneratingAudio,
            AutonomousGenerationRunStatus.Finalizing => AutonomousGenerationStage.Finalizing,
            AutonomousGenerationRunStatus.Completed => AutonomousGenerationStage.Completed,
            _ => summary.CurrentStage
        };
    }

    private static AutonomousGenerationStage ResolveStageFromCheckpoint(AutonomousProjectCheckpoint checkpoint)
    {
        if (!checkpoint.HasValidStory)
        {
            return AutonomousGenerationStage.GeneratingStoryNarrative;
        }

        if (!checkpoint.HasValidCharacters)
        {
            return AutonomousGenerationStage.GeneratingCharacters;
        }

        if (checkpoint.FirstMissingNarrativeSceneNumber is not null)
        {
            return AutonomousGenerationStage.GeneratingNarrativeScenes;
        }

        if (checkpoint.FirstMissingImagePromptSceneNumber is not null)
        {
            return AutonomousGenerationStage.GeneratingImagePrompts;
        }

        if (checkpoint.FirstMissingVideoPromptSceneNumber is not null)
        {
            return AutonomousGenerationStage.GeneratingVideoPrompts;
        }

        if (checkpoint.FirstMissingSelectedImageSceneNumber is not null)
        {
            return AutonomousGenerationStage.GeneratingImages;
        }

        if (checkpoint.FirstMissingSelectedVideoSceneNumber is not null)
        {
            return AutonomousGenerationStage.GeneratingVideos;
        }

        if (checkpoint.FirstMissingSceneAudioSceneNumber is not null)
        {
            return AutonomousGenerationStage.GeneratingAudio;
        }

        return AutonomousGenerationStage.Completed;
    }

    private static bool IsStoryWorkspaceStage(AutonomousGenerationStage stage) =>
        stage is AutonomousGenerationStage.Pending or
            AutonomousGenerationStage.Validating or
            AutonomousGenerationStage.GeneratingStory or
            AutonomousGenerationStage.GeneratingScenes or
            AutonomousGenerationStage.GeneratingStoryNarrative or
            AutonomousGenerationStage.GeneratingCharacters or
            AutonomousGenerationStage.GeneratingNarrativeScenes or
            AutonomousGenerationStage.GeneratingImagePrompts or
            AutonomousGenerationStage.GeneratingVideoPrompts;
}
