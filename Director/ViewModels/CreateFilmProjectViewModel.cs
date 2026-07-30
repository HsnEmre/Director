using System.Windows.Input;
using Director.Commands;
using Director.Enums;
using Director.Helpers;
using Director.Models;
using Director.Services.Interfaces;

namespace Director.ViewModels;

public class CreateFilmProjectViewModel : ValidatableObservableObject
{
    private readonly IFilmProjectService _filmProjectService;
    private readonly IMessageService _messageService;
    private readonly INavigationService _navigationService;
    private bool _isInitializing;
    private bool _hasUnsavedChanges;

    private int? _currentProjectId;
    private string _projectName = string.Empty;
    private string _subject = string.Empty;
    private int _totalDurationMinutes = 20;
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
    private string? _narratorTone;
    private string? _mainCharacterDescription;
    private string? _additionalInstructions;
    private bool _isBusy;
    private string _statusMessage = "Yeni bir film projesi taslağı hazırlayın.";
    private string _aspectRatioWarningText = string.Empty;

    public CreateFilmProjectViewModel(
        IFilmProjectService filmProjectService,
        IMessageService messageService,
        INavigationService navigationService)
    {
        _filmProjectService = filmProjectService;
        _messageService = messageService;
        _navigationService = navigationService;

        ClipDurationOptions = new List<int> { 5, 10, 15 };
        LanguageOptions = new List<string> { "Türkçe", "İngilizce", "Almanca", "Fransızca", "İspanyolca" };
        TargetAudienceOptions = new List<string> { "Çocuk", "Genç", "Yetişkin", "Aile", "Genel İzleyici" };
        StoryGenreOptions = new List<string> { "Macera", "Fantastik", "Bilim Kurgu", "Dram", "Komedi", "Korku", "Gerilim", "Belgesel", "Eğitici", "Masal" };
        VisualStyleOptions = new List<string> { "Sinematik Gerçekçi", "3D Animasyon", "2D Animasyon", "Anime", "Masal Kitabı İllüstrasyonu", "Stop Motion", "Karanlık Fantastik", "Belgesel Gerçekçiliği" };
        VideoStyleOptions = new List<string> { "Sinematik", "Yavaş ve Atmosferik", "Dinamik", "Belgesel", "Çocuk Animasyonu", "Reklam Filmi", "Müzik Videosu" };
        AspectRatioOptions = new List<string> { "16:9", "9:16", "1:1", "4:3", "21:9" };
        ResolutionOptions = new List<string> { "1280x720", "1920x1080", "1080x1920", "1024x1024" };
        NarratorToneOptions = new List<string> { "Sakin ve sıcak", "Masalsı", "Dramatik", "Belgesel anlatımı", "Enerjik", "Gizemli" };

        SaveDraftCommand = new AsyncRelayCommand(() => SaveAsync(FilmProjectStatus.Draft), () => !IsBusy);
        ContinueCommand = new AsyncRelayCommand(() => SaveAsync(FilmProjectStatus.ReadyForStoryGeneration), () => !IsBusy);
        ClearFormCommand = new RelayCommand(ClearForm, () => !IsBusy);

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

    public string ProjectNameError => GetFirstError(nameof(ProjectName));
    public string SubjectError => GetFirstError(nameof(Subject));
    public string TotalDurationMinutesError => GetFirstError(nameof(TotalDurationMinutes));
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

    public async Task LoadProjectAsync(int projectId, CancellationToken cancellationToken = default)
    {
        var project = await _filmProjectService.GetByIdAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadı.");

        _isInitializing = true;
        CurrentProjectId = project.Id;
        ProjectName = project.ProjectName;
        Subject = project.Subject;
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
        NarratorTone = project.NarratorTone;
        MainCharacterDescription = project.MainCharacterDescription;
        AdditionalInstructions = project.AdditionalInstructions;
        StatusMessage = "Kayıtlı proje ayarları yüklendi.";
        _isInitializing = false;
        _hasUnsavedChanges = false;
        ValidateAll();
    }

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
        project.TotalDurationMinutes = TotalDurationMinutes;
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
        TotalDurationMinutes = 20;
        ClipDurationSeconds = 10;
        Language = "Türkçe";
        TargetAudience = "Genel İzleyici";
        StoryGenre = string.Empty;
        VisualStyle = string.Empty;
        VideoStyle = string.Empty;
        AspectRatio = "16:9";
        Resolution = "1920x1080";
        UseNarrator = false;
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
        if (TotalDurationMinutes <= 0 || ClipDurationSeconds <= 0)
        {
            CalculatedClipCount = 0;
            CalculatedOutputDurationText = "Hesaplanamadı";
            DurationWarningText = string.Empty;
            return;
        }

        var totalSeconds = TotalDurationMinutes * 60;
        CalculatedClipCount = (int)Math.Ceiling(totalSeconds / (double)ClipDurationSeconds);
        var outputSeconds = CalculatedClipCount * ClipDurationSeconds;
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
        if (TotalDurationMinutes < 1 || TotalDurationMinutes > 180)
        {
            errors.Add("Toplam süre 1 ile 180 dakika arasında olmalıdır.");
        }

        SetErrors(nameof(TotalDurationMinutes), errors);
        OnPropertyChanged(nameof(TotalDurationMinutesError));
    }

    private void ValidateClipDurationSeconds()
    {
        var errors = ClipDurationOptions.Contains(ClipDurationSeconds)
            ? Enumerable.Empty<string>()
            : new[] { "Klip süresi yalnızca 5, 10 veya 15 saniye olabilir." };

        SetErrors(nameof(ClipDurationSeconds), errors);
        OnPropertyChanged(nameof(ClipDurationSecondsError));
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
    }
}
