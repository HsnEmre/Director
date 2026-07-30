using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Security.Cryptography;
using System.Text;
using Director.Commands;
using Director.Data;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Helpers;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Director.ViewModels;

public sealed class ProductionWorkspaceViewModel : ObservableObject
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpRuntimeCoordinator _runtimeCoordinator;
    private readonly IWanGpLocalModelInventoryService _inventoryService;
    private readonly IWanGpOutputResolver _outputResolver;
    private readonly IImageGenerationService _imageGenerationService;
    private readonly IVideoPromptComposerService _videoPromptComposerService;
    private readonly IOllamaModelLifecycleService _ollamaModelLifecycleService;
    private readonly IVideoGenerationService _videoGenerationService;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IApplicationActivityCenter _activityCenter;
    private readonly WanGpOptions _options;
    private CancellationTokenSource? _generationCancellation;
    private int _filmProjectId;
    private string _projectName = string.Empty;
    private string _connectionStatus = "WanGP henuz test edilmedi.";
    private string _guiStatus = "WanGP arayuzu kontrol edilmedi.";
    private string _mcpStatus = "WanGP MCP kontrol edilmedi.";
    private string _modelStatus = "Modeller henuz yuklenmedi.";
    private string _selectedModelType = string.Empty;
    private string _selectedResolution = "1024x1024";
    private string _selectedVideoResolution = "1024x1024";
    private int _inferenceSteps = 20;
    private int _videoInferenceSteps = 12;
    private int? _seed;
    private int? _videoSeed;
    private bool _randomSeed = true;
    private bool _videoRandomSeed = true;
    private bool _isBusy;
    private double _progressPercentage;
    private ImageSource? _previewImageSource;
    private string _previewStatus = "Secili gorsel burada goruntulenecek.";
    private bool _showLogs = true;
    private ProductionSceneRowViewModel? _selectedScene;
    private SceneMediaAssetRowViewModel? _selectedAsset;
    private WanGpModelOptionViewModel? _selectedModel;
    private WanGpVideoModelOptionViewModel? _selectedVideoModel;
    private WanGpVideoConfigurationOptionViewModel? _selectedVideoConfiguration;
    private WanGpOutputCandidate? _selectedWanGpOutput;
    private SceneMediaAssetRowViewModel? _selectedVideoAsset;
    private string _preparedVideoPrompt = string.Empty;
    private string _preparedVideoNegativePrompt = string.Empty;
    private string _videoStatus = "Video hazir degil.";
    private Uri? _videoPreviewSource;

    public ProductionWorkspaceViewModel(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IWanGpClient wanGpClient,
        IWanGpRuntimeCoordinator runtimeCoordinator,
        IWanGpLocalModelInventoryService inventoryService,
        IWanGpOutputResolver outputResolver,
        IImageGenerationService imageGenerationService,
        IVideoPromptComposerService videoPromptComposerService,
        IOllamaModelLifecycleService ollamaModelLifecycleService,
        IVideoGenerationService videoGenerationService,
        IGpuGenerationCoordinator gpuCoordinator,
        IApplicationActivityCenter activityCenter,
        IOptions<WanGpOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _wanGpClient = wanGpClient;
        _runtimeCoordinator = runtimeCoordinator;
        _inventoryService = inventoryService;
        _outputResolver = outputResolver;
        _imageGenerationService = imageGenerationService;
        _videoPromptComposerService = videoPromptComposerService;
        _ollamaModelLifecycleService = ollamaModelLifecycleService;
        _videoGenerationService = videoGenerationService;
        _gpuCoordinator = gpuCoordinator;
        _activityCenter = activityCenter;
        _options = options.Value;

        Scenes = new ObservableCollection<ProductionSceneRowViewModel>();
        Assets = new ObservableCollection<SceneMediaAssetRowViewModel>();
        InstalledModels = new ObservableCollection<WanGpModelOptionViewModel>();
        OtherModels = new ObservableCollection<WanGpModelOptionViewModel>();
        WanGpOutputs = new ObservableCollection<WanGpOutputCandidate>();
        VideoAssets = new ObservableCollection<SceneMediaAssetRowViewModel>();
        InstalledVideoModels = new ObservableCollection<WanGpVideoModelOptionViewModel>();
        OtherVideoModels = new ObservableCollection<WanGpVideoModelOptionViewModel>();
        VideoConfigurations = new ObservableCollection<WanGpVideoConfigurationOptionViewModel>();
        Resolutions = new ObservableCollection<string> { "1024x1024", "768x768", "512x512", "1280x720", "1920x1080" };
        Logs = _activityCenter.Logs;

        TestConnectionCommand = new AsyncRelayCommand(EnsureReadyAndLoadModelsAsync, () => !IsBusy);
        RefreshModelsCommand = new AsyncRelayCommand(() => EnsureReadyAndLoadModelsAsync(forceRefresh: true), () => !IsBusy);
        ReconnectCommand = new AsyncRelayCommand(EnsureReadyAndLoadModelsAsync, () => !IsBusy);
        OpenGuiCommand = new RelayCommand(OpenGui);
        GenerateSelectedCommand = new AsyncRelayCommand(GenerateSelectedAsync, CanGenerate);
        GenerateMissingCommand = new AsyncRelayCommand(GenerateMissingAsync, () => !IsBusy && Scenes.Count > 0 && SelectedModel is { IsSelectable: true });
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsBusy);
        OpenFolderCommand = new RelayCommand(OpenSelectedFolder, () => SelectedScene is not null);
        OpenWanGpOutputFolderCommand = new RelayCommand(OpenWanGpOutputFolder);
        OpenDirectorImagesFolderCommand = new RelayCommand(OpenSelectedFolder, () => SelectedScene is not null);
        SelectAssetCommand = new AsyncRelayCommand(SelectAssetAsync, () => !IsBusy && SelectedAsset is not null);
        ScanWanGpOutputsCommand = new AsyncRelayCommand(ScanWanGpOutputsAsync, () => !IsBusy);
        ImportOutputCommand = new AsyncRelayCommand(ImportSelectedOutputAsync, () => !IsBusy && SelectedScene is not null && SelectedWanGpOutput is not null);
        ToggleLogsCommand = new RelayCommand(() => ShowLogs = !ShowLogs);
        PrepareVideoPromptCommand = new AsyncRelayCommand(PrepareVideoPromptAsync, CanPrepareVideoPrompt);
        ResetPreparedVideoPromptCommand = new RelayCommand(() => PreparedVideoPrompt = SelectedScene?.VideoPrompt ?? string.Empty, () => SelectedScene is not null);
        UpdateSceneVideoPromptCommand = new AsyncRelayCommand(UpdateSceneVideoPromptAsync, () => !IsBusy && SelectedScene is not null && !string.IsNullOrWhiteSpace(PreparedVideoPrompt));
        GenerateVideoCommand = new AsyncRelayCommand(GenerateVideoAsync, CanGenerateVideo);
        SelectVideoAssetCommand = new AsyncRelayCommand(SelectVideoAssetAsync, () => !IsBusy && SelectedVideoAsset is not null);
        RefreshVideoModelsCommand = new AsyncRelayCommand(() => RefreshVideoModelsFromUiAsync(forceRefresh: true, CancellationToken.None), () => !IsBusy);

        _activityCenter.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ConnectionStatus));
            OnPropertyChanged(nameof(ActivityStatus));
            OnPropertyChanged(nameof(ActivityStartedAt));
            OnPropertyChanged(nameof(ActivityLastAt));
            OnPropertyChanged(nameof(ActivityElapsed));
            OnPropertyChanged(nameof(ActivityStep));
            RaiseCommandStates();
        };
    }

    public int FilmProjectId { get => _filmProjectId; private set => SetProperty(ref _filmProjectId, value); }
    public string ProjectName { get => _projectName; private set => SetProperty(ref _projectName, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public string GuiStatus { get => _guiStatus; private set => SetProperty(ref _guiStatus, value); }
    public string McpStatus { get => _mcpStatus; private set => SetProperty(ref _mcpStatus, value); }
    public string ModelStatus { get => _modelStatus; private set => SetProperty(ref _modelStatus, value); }
    public string GuiUrl => _options.GuiUrl;
    public string McpEndpoint => _options.Endpoint;
    public string SelectedModelType { get => _selectedModelType; private set => SetProperty(ref _selectedModelType, value); }
    public string SelectedResolution { get => _selectedResolution; set { if (SetProperty(ref _selectedResolution, value)) OnPropertyChanged(nameof(SelectedModelDetails)); } }
    public string SelectedVideoResolution { get => _selectedVideoResolution; set { if (SetProperty(ref _selectedVideoResolution, value)) RaiseCommandStates(); } }
    public int InferenceSteps { get => _inferenceSteps; set { if (SetProperty(ref _inferenceSteps, Math.Max(1, value))) OnPropertyChanged(nameof(SelectedModelDetails)); } }
    public int VideoInferenceSteps { get => _videoInferenceSteps; set => SetProperty(ref _videoInferenceSteps, Math.Max(1, value)); }
    public int? Seed { get => _seed; set => SetProperty(ref _seed, value); }
    public int? VideoSeed { get => _videoSeed; set => SetProperty(ref _videoSeed, value); }
    public bool RandomSeed { get => _randomSeed; set => SetProperty(ref _randomSeed, value); }
    public bool VideoRandomSeed { get => _videoRandomSeed; set => SetProperty(ref _videoRandomSeed, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsGenerating));
                RaiseCommandStates();
            }
        }
    }
    public bool IsGenerating => IsBusy;
    public double ProgressPercentage { get => _progressPercentage; private set => SetProperty(ref _progressPercentage, value); }
    public ProductionSceneRowViewModel? SelectedScene
    {
        get => _selectedScene;
        set
        {
            if (SetProperty(ref _selectedScene, value))
            {
                LoadAssetsFromSelectedScene();
                LoadVideoAssetsFromSelectedScene();
                PreparedVideoPrompt = value?.VideoPrompt ?? string.Empty;
                PreparedVideoNegativePrompt = value?.VideoNegativePrompt ?? string.Empty;
                OnPropertyChanged(nameof(SelectedSceneDisplay));
                if (value is not null)
                {
                    AddLog("Sahne", $"Sahne {value.SceneNumber} secildi.");
                }
                RaiseCommandStates();
            }
        }
    }

    public WanGpVideoModelOptionViewModel? SelectedVideoModel
    {
        get => _selectedVideoModel;
        set
        {
            if (SetProperty(ref _selectedVideoModel, value))
            {
                OnPropertyChanged(nameof(SelectedVideoModelDetails));
                LoadVideoConfigurations(value);
                RaiseCommandStates();
            }
        }
    }

    public WanGpVideoConfigurationOptionViewModel? SelectedVideoConfiguration
    {
        get => _selectedVideoConfiguration;
        set
        {
            if (SetProperty(ref _selectedVideoConfiguration, value))
            {
                OnPropertyChanged(nameof(SelectedVideoModelDetails));
                RaiseCommandStates();
            }
        }
    }

    public SceneMediaAssetRowViewModel? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                RaiseCommandStates();
                OnPropertyChanged(nameof(PreviewPath));
                RefreshPreviewImage();
            }
        }
    }

    public WanGpModelOptionViewModel? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (SetProperty(ref _selectedModel, value))
            {
                SelectedModelType = value?.ModelType ?? string.Empty;
                _activityCenter.Snapshot.SelectedModel = value?.DisplayText ?? string.Empty;
                OnPropertyChanged(nameof(SelectedModelDetails));
                if (value is not null)
                {
                    _ = LoadModelSchemaAsync(value.ModelType);
                }

                RaiseCommandStates();
            }
        }
    }

    public string? PreviewPath => SelectedAsset?.FilePath ?? SelectedScene?.SelectedImagePath;
    public ImageSource? PreviewImageSource { get => _previewImageSource; private set => SetProperty(ref _previewImageSource, value); }
    public bool HasPreview => PreviewImageSource is not null;
    public string PreviewStatus { get => _previewStatus; private set => SetProperty(ref _previewStatus, value); }
    public bool ShowLogs { get => _showLogs; set => SetProperty(ref _showLogs, value); }
    public string SelectedSceneDisplay => SelectedScene is null ? "-" : SelectedScene.SceneNumber.ToString("000");
    public string SelectedModelDetails => SelectedModel is null
        ? "Model secilmedi."
        : $"Ad: {SelectedModel.DisplayName}\nmodel_type: {SelectedModel.ModelType}\nCheckpoint: {SelectedModel.CheckpointPath ?? "-"}\nDurum: {SelectedModel.InstallationStatus}\nCozunurluk: {SelectedResolution}\nInference step: {InferenceSteps}";
    public string ActivityStatus => _activityCenter.Snapshot.OperationStatus?.ToString() ?? "Hazir";
    public string ActivityStartedAt => _activityCenter.Snapshot.StartedAt?.ToString("HH:mm:ss") ?? "-";
    public string ActivityLastAt => _activityCenter.Snapshot.LastActivityAt?.ToString("HH:mm:ss") ?? "-";
    public string ActivityElapsed
    {
        get
        {
            var started = _activityCenter.Snapshot.StartedAt;
            if (started is null)
            {
                return "-";
            }

            var elapsed = DateTime.Now - started.Value;
            return elapsed.ToString(@"mm\:ss");
        }
    }
    public string ActivityStep => _activityCenter.Snapshot.CurrentStep is int current && _activityCenter.Snapshot.TotalSteps is int total
        ? $"{current} / {total}"
        : "Sunucu yuzde/adim bilgisi iletmedi.";
    public string PreparedVideoPrompt { get => _preparedVideoPrompt; set { if (SetProperty(ref _preparedVideoPrompt, value)) RaiseCommandStates(); } }
    public string PreparedVideoNegativePrompt { get => _preparedVideoNegativePrompt; set => SetProperty(ref _preparedVideoNegativePrompt, value); }
    public string VideoStatus { get => _videoStatus; private set => SetProperty(ref _videoStatus, value); }
    public Uri? VideoPreviewSource { get => _videoPreviewSource; private set => SetProperty(ref _videoPreviewSource, value); }
    public string SelectedVideoModelDetails => SelectedVideoModel is null
        ? "Video modeli secilmedi."
        : $"Prompt Hazirlama Modeli: qwen3-vl:30b-a3b-instruct\nVideo Uretim Modeli: {SelectedVideoModel.DisplayText}\nConfig: {SelectedVideoConfiguration?.DisplayText ?? "-"}\nmodel_type: {SelectedVideoModel.ModelType}\nArchitecture: {SelectedVideoModel.Architecture}\nCheckpoint: {SelectedVideoConfiguration?.CheckpointPath ?? SelectedVideoModel.CheckpointPath ?? "-"}\nDurum: {SelectedVideoModel.Availability}";

    public ObservableCollection<ProductionSceneRowViewModel> Scenes { get; }
    public ObservableCollection<SceneMediaAssetRowViewModel> Assets { get; }
    public ObservableCollection<WanGpModelOptionViewModel> InstalledModels { get; }
    public ObservableCollection<WanGpModelOptionViewModel> OtherModels { get; }
    public ObservableCollection<WanGpOutputCandidate> WanGpOutputs { get; }
    public ObservableCollection<SceneMediaAssetRowViewModel> VideoAssets { get; }
    public ObservableCollection<WanGpVideoModelOptionViewModel> InstalledVideoModels { get; }
    public ObservableCollection<WanGpVideoModelOptionViewModel> OtherVideoModels { get; }
    public ObservableCollection<WanGpVideoConfigurationOptionViewModel> VideoConfigurations { get; }
    public ObservableCollection<string> Resolutions { get; }
    public ObservableCollection<ProductionLogEntry> Logs { get; }

    public ICommand TestConnectionCommand { get; }
    public ICommand RefreshModelsCommand { get; }
    public ICommand ReconnectCommand { get; }
    public ICommand OpenGuiCommand { get; }
    public ICommand GenerateSelectedCommand { get; }
    public ICommand GenerateMissingCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenWanGpOutputFolderCommand { get; }
    public ICommand OpenDirectorImagesFolderCommand { get; }
    public ICommand SelectAssetCommand { get; }
    public ICommand ScanWanGpOutputsCommand { get; }
    public ICommand ImportOutputCommand { get; }
    public ICommand ToggleLogsCommand { get; }
    public ICommand PrepareVideoPromptCommand { get; }
    public ICommand ResetPreparedVideoPromptCommand { get; }
    public ICommand UpdateSceneVideoPromptCommand { get; }
    public ICommand GenerateVideoCommand { get; }
    public ICommand SelectVideoAssetCommand { get; }
    public ICommand RefreshVideoModelsCommand { get; }

    public WanGpOutputCandidate? SelectedWanGpOutput
    {
        get => _selectedWanGpOutput;
        set
        {
            if (SetProperty(ref _selectedWanGpOutput, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public SceneMediaAssetRowViewModel? SelectedVideoAsset
    {
        get => _selectedVideoAsset;
        set
        {
            if (SetProperty(ref _selectedVideoAsset, value))
            {
                VideoPreviewSource = string.IsNullOrWhiteSpace(value?.FilePath) ? null : new Uri(value.FilePath, UriKind.Absolute);
                RaiseCommandStates();
            }
        }
    }

    public async Task InitializeAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        FilmProjectId = filmProjectId;
        await LoadProjectAsync(cancellationToken);
        await LoadScenesAsync(cancellationToken);
        AddLog("Hazir", "Gorsel uretim calisma alani acildi.");
        await EnsureReadyAndLoadModelsAsync(cancellationToken: cancellationToken);
        RaiseCommandStates();
    }
    public async Task EnsureVideoModelsLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (InstalledVideoModels.Count > 0 || IsBusy)
        {
            return;
        }

        await RefreshVideoModelsFromUiAsync(forceRefresh: false, cancellationToken);
    }

    private async Task RefreshVideoModelsFromUiAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            AddLog("VideoModel", "Video model kesfi baslatildi.");
            AddLog("VideoModel", "WanGP MCP baglantisi dogrulaniyor.");
            var runtime = await _runtimeCoordinator.EnsureReadyAsync(cancellationToken);
            GuiStatus = runtime.GuiState == WanGpGuiState.Open
                ? $"Acik - {_options.GuiUrl}"
                : $"Kapali - {_options.GuiUrl}";
            McpStatus = $"{runtime.McpState} - {_options.Endpoint}";
            ConnectionStatus = runtime.Message;
            if (!runtime.IsReady)
            {
                VideoStatus = runtime.McpState == WanGpMcpConnectionState.PortConflict
                    ? "7866 portu acik fakat MCP handshake basarisiz."
                    : "MCP baglantisi yok; video modelleri yuklenemedi.";
                AddLog("VideoModel", VideoStatus, GenerationLogLevel.Warning);
                return;
            }

            await LoadVideoModelsAsync(forceRefresh, cancellationToken);
        }
        catch (Exception ex)
        {
            VideoStatus = ex.Message;
            ModelStatus = ex.Message;
            AddLog("VideoModel", ex.Message, GenerationLogLevel.Error);
            _activityCenter.SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnsureReadyAndLoadModelsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureReadyAndLoadModelsAsync(false, cancellationToken);
    }

    private async Task EnsureReadyAndLoadModelsAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            ConnectionStatus = "WanGP hazirlaniyor...";
            ModelStatus = "Modeller yukleniyor...";
            var runtime = await _runtimeCoordinator.EnsureReadyAsync(cancellationToken);
            GuiStatus = runtime.GuiState == WanGpGuiState.Open
                ? $"Acik - {_options.GuiUrl}"
                : $"Kapali - {_options.GuiUrl}";
            McpStatus = $"{runtime.McpState} - {_options.Endpoint}";
            ConnectionStatus = runtime.Message;
            if (!runtime.IsReady)
            {
                ModelStatus = runtime.McpState == WanGpMcpConnectionState.PortConflict
                    ? "7866 portu acik fakat MCP handshake basarisiz."
                    : "MCP baglantisi yok; modeller yuklenemedi.";
                return;
            }

            await LoadModelsAsync(forceRefresh, cancellationToken);
            await LoadVideoModelsAsync(forceRefresh, cancellationToken);
        }
        catch (Exception ex)
        {
            ConnectionStatus = ex.Message;
            ModelStatus = ex.Message;
            _activityCenter.SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GenerateSelectedAsync()
    {
        if (SelectedScene is null)
        {
            return;
        }

        IsBusy = true;
        ProgressPercentage = 0;
        _generationCancellation = new CancellationTokenSource();
        var sceneNumber = SelectedScene.SceneNumber;
        try
        {
            _activityCenter.StartOperation("WanGP gorsel uretimi", FilmProjectId, ProjectName, SelectedScene.Id, SelectedScene.SceneNumber);
            var request = BuildRequest(SelectedScene);
            var progress = new Progress<MediaGenerationProgress>(OnProgressChanged);
            await _imageGenerationService.GenerateSceneImageAsync(SelectedScene.Id, request, progress, _generationCancellation.Token);
            _activityCenter.CompleteOperation(GenerationJobStatus.Completed, "Gorsel uretimi tamamlandi.");
            await LoadScenesAsync(CancellationToken.None);
            SelectedScene = Scenes.FirstOrDefault(scene => scene.SceneNumber == sceneNumber);
            RefreshPreviewImage();
            AddLog("Gorsel", $"Sahne {sceneNumber} gorseli hazir.", GenerationLogLevel.Success);
        }
        catch (OperationCanceledException)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Cancelled, "Gorsel uretimi iptal edildi.");
            AddLog("Iptal", "Gorsel uretimi iptal edildi.", GenerationLogLevel.Warning);
        }
        catch (Exception ex)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Failed, ex.Message);
            AddLog("Hata", ex.Message, GenerationLogLevel.Error);
            throw;
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task GenerateMissingAsync()
    {
        IsBusy = true;
        ProgressPercentage = 0;
        _generationCancellation = new CancellationTokenSource();
        try
        {
            var request = BuildRequest(SelectedScene);
            var progress = new Progress<MediaGenerationProgress>(OnProgressChanged);
            await _imageGenerationService.GenerateMissingImagesAsync(FilmProjectId, request, request.StopOnError, progress, _generationCancellation.Token);
            await LoadScenesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Cancelled, "Toplu gorsel uretimi iptal edildi.");
            AddLog("Iptal", "Toplu gorsel uretimi iptal edildi.", GenerationLogLevel.Warning);
        }
        catch (Exception ex)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Failed, ex.Message);
            AddLog("Hata", ex.Message, GenerationLogLevel.Error);
            throw;
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task CancelAsync()
    {
        _generationCancellation?.Cancel();
        await _imageGenerationService.CancelActiveJobAsync();
        await _videoGenerationService.CancelActiveJobAsync();
        AddLog("Iptal", "Aktif WanGP isi icin iptal istegi gonderildi.", GenerationLogLevel.Warning);
    }

    private async Task SelectAssetAsync()
    {
        if (SelectedAsset is null)
        {
            return;
        }

        var assetId = SelectedAsset.Id;
        var sceneNumber = SelectedScene?.SceneNumber;
        await _imageGenerationService.SetSelectedAssetAsync(assetId);
        await LoadScenesAsync();
        SelectedScene = Scenes.FirstOrDefault(scene => scene.SceneNumber == sceneNumber) ?? SelectedScene;
        SelectedAsset = Assets.FirstOrDefault(asset => asset.Id == assetId) ?? SelectedAsset;
        AddLog("Versiyon", "Secili gorsel guncellendi.", GenerationLogLevel.Success);
    }

    private async Task ScanWanGpOutputsAsync()
    {
        WanGpOutputs.Clear();
        AddLog("Output", "WanGP output klasoru taraniyor.");
        var outputs = await _outputResolver.ScanExistingImageOutputsAsync();
        foreach (var output in outputs)
        {
            WanGpOutputs.Add(output);
        }

        AddLog("Output", $"{WanGpOutputs.Count} gorsel output bulundu.", WanGpOutputs.Count > 0 ? GenerationLogLevel.Success : GenerationLogLevel.Warning);
    }

    private async Task ImportSelectedOutputAsync()
    {
        if (SelectedScene is null || SelectedWanGpOutput is null)
        {
            return;
        }

        IsBusy = true;
        var sceneNumber = SelectedScene.SceneNumber;
        try
        {
            AddLog("Import", $"{SelectedWanGpOutput.DisplayName} secili sahneye aktariliyor.");
            var asset = await _imageGenerationService.ImportExistingWanGpOutputAsync(SelectedScene.Id, SelectedWanGpOutput.FilePath, true);
            await LoadScenesAsync();
            SelectedScene = Scenes.FirstOrDefault(scene => scene.SceneNumber == sceneNumber);
            SelectedAsset = Assets.FirstOrDefault(item => item.Id == asset.Id);
            RefreshPreviewImage();
            AddLog("Import", "Output Director proje klasorune kopyalandi ve onizleme hazir.", GenerationLogLevel.Success);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadProjectAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.FilmProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == FilmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadi.");
        ProjectName = project.ProjectName;
    }

    private async Task LoadScenesAsync(CancellationToken cancellationToken = default)
    {
        var selectedSceneNumber = SelectedScene?.SceneNumber;
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == FilmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .Select(scene => new ProductionSceneRowViewModel
            {
                Id = scene.Id,
                SceneNumber = scene.SceneNumber,
                Title = scene.Title,
                ImagePrompt = scene.ImagePrompt,
                ImageNegativePrompt = scene.ImageNegativePrompt,
                VideoPrompt = scene.VideoPrompt,
                VideoNegativePrompt = scene.VideoNegativePrompt,
                DurationSeconds = scene.DurationSeconds,
                SelectedImagePath = scene.MediaAssets
                    .Where(asset => asset.MediaType == MediaType.Image && asset.IsSelected)
                    .Select(asset => asset.FilePath)
                    .FirstOrDefault(),
                ImageCount = scene.MediaAssets.Count(asset => asset.MediaType == MediaType.Image),
                LastStatus = scene.GenerationJobs
                    .Where(job => job.MediaType == MediaType.Image)
                    .OrderByDescending(job => job.CreatedAt)
                    .Select(job => job.Status.ToString())
                    .FirstOrDefault() ?? "Hazir",
                Assets = scene.MediaAssets
                    .Where(asset => asset.MediaType == MediaType.Image)
                    .OrderByDescending(asset => asset.VersionNumber)
                    .Select(asset => new SceneMediaAssetRowViewModel
                    {
                        Id = asset.Id,
                        FilePath = asset.FilePath,
                        ThumbnailPath = asset.ThumbnailPath,
                        VersionNumber = asset.VersionNumber,
                        IsSelected = asset.IsSelected,
                        ModelType = asset.ModelType,
                        CreatedAt = asset.CreatedAt,
                        Seed = asset.Seed
                    })
                    .ToList()
                ,
                VideoAssets = scene.MediaAssets
                    .Where(asset => asset.MediaType == MediaType.Video)
                    .OrderByDescending(asset => asset.VersionNumber)
                    .Select(asset => new SceneMediaAssetRowViewModel
                    {
                        Id = asset.Id,
                        FilePath = asset.FilePath,
                        ThumbnailPath = asset.ThumbnailPath,
                        VersionNumber = asset.VersionNumber,
                        IsSelected = asset.IsSelected,
                        ModelType = asset.ModelType,
                        CreatedAt = asset.CreatedAt,
                        Seed = asset.Seed,
                        DurationSeconds = asset.DurationSeconds
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        Scenes.Clear();
        foreach (var row in rows)
        {
            Scenes.Add(row);
        }

        SelectedScene = Scenes.FirstOrDefault(scene => scene.SceneNumber == selectedSceneNumber) ?? Scenes.FirstOrDefault();
        OnPropertyChanged(nameof(PreviewPath));
    }

    private void LoadAssetsFromSelectedScene()
    {
        Assets.Clear();
        if (SelectedScene is not null)
        {
            foreach (var asset in SelectedScene.Assets)
            {
                Assets.Add(asset);
            }
        }

        SelectedAsset = Assets.FirstOrDefault(asset => asset.IsSelected) ?? Assets.FirstOrDefault();
        OnPropertyChanged(nameof(PreviewPath));
    }

    private void LoadVideoAssetsFromSelectedScene()
    {
        VideoAssets.Clear();
        if (SelectedScene is not null)
        {
            foreach (var asset in SelectedScene.VideoAssets)
            {
                VideoAssets.Add(asset);
            }
        }

        SelectedVideoAsset = VideoAssets.FirstOrDefault(asset => asset.IsSelected) ?? VideoAssets.FirstOrDefault();
        VideoStatus = SelectedVideoAsset is null ? "Bu sahne icin video yok." : "Secili video yuklendi.";
    }

    private async Task LoadModelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        _activityCenter.SetModelDiscoveryStatus("Modeller yukleniyor...");
        var supportedModels = await _wanGpClient.GetAvailableImageModelsAsync(cancellationToken);
        AddLog("Modeller", $"{supportedModels.Count} desteklenen image modeli alindi.");
        var inventory = await _inventoryService.GetInventoryAsync(supportedModels, forceRefresh, cancellationToken);
        var merged = supportedModels.Select(model =>
        {
            inventory.TryGetValue(model.ModelType, out var item);
            return new WanGpModelOptionViewModel
            {
                ModelType = model.ModelType,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelType : model.DisplayName,
                Family = model.Family,
                MainOutput = model.MainOutput,
                Inputs = model.Inputs,
                InstallationStatus = item?.Status ?? WanGpModelInstallStatus.Unknown,
                CheckpointPath = item?.CheckpointPath,
                CheckedAt = item?.CheckedAt ?? DateTime.Now,
                SupportsTextToImage = model.Inputs.Contains("text", StringComparison.OrdinalIgnoreCase),
                SupportsReferenceImage = model.Inputs.Contains("image", StringComparison.OrdinalIgnoreCase)
            };
        }).OrderBy(model => model.DisplayName).ToList();

        var current = SelectedModel?.ModelType;
        InstalledModels.Clear();
        OtherModels.Clear();
        foreach (var model in merged)
        {
            if (model.InstallationStatus == WanGpModelInstallStatus.Installed)
            {
                InstalledModels.Add(model);
            }
            else
            {
                OtherModels.Add(model);
            }
        }

        SelectedModel = InstalledModels.FirstOrDefault(model => model.ModelType == current)
            ?? InstalledModels.FirstOrDefault(model => model.ModelType == "qwen_image_20B")
            ?? InstalledModels.FirstOrDefault();

        var partial = merged.Count(model => model.InstallationStatus == WanGpModelInstallStatus.Partial);
        var missing = merged.Count(model => model.InstallationStatus == WanGpModelInstallStatus.Missing);
        var unknown = merged.Count(model => model.InstallationStatus == WanGpModelInstallStatus.Unknown);
        ModelStatus = InstalledModels.Count == 0
            ? "MCP modelleri dondu ancak yerel kurulu image modeli bulunamadi."
            : $"{InstalledModels.Count} kurulu, {partial} kismi, {missing} eksik, {unknown} bilinmeyen model bulundu.";
        _activityCenter.SetModelDiscoveryStatus(ModelStatus);
        RaiseCommandStates();
    }

    private async Task LoadModelSchemaAsync(string modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType))
        {
            return;
        }

        try
        {
            var schema = await _wanGpClient.GetModelSchemaAsync(modelType);
            if (schema is null)
            {
                return;
            }

            Resolutions.Clear();
            foreach (var resolution in schema.SupportedResolutions.DefaultIfEmpty("1024x1024"))
            {
                Resolutions.Add(resolution);
            }

            SelectedResolution = Resolutions.FirstOrDefault() ?? "1024x1024";
            InferenceSteps = schema.DefaultInferenceSteps <= 0 ? InferenceSteps : schema.DefaultInferenceSteps;
            OnPropertyChanged(nameof(SelectedModelDetails));
        }
        catch (Exception ex)
        {
            AddLog("Schema", ex.Message, GenerationLogLevel.Warning);
        }
    }

    private async Task LoadVideoModelsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        AddLog("VideoModel", "WanGP video modelleri sorgulaniyor.");
        var models = await _wanGpClient.GetAvailableImageToVideoModelsAsync(cancellationToken);
        AddLog("VideoModel", $"MCP'den {models.Count} image-to-video aday modeli geldi.");
        AddLog("VideoModel", "Model metadata degerleri inceleniyor.");
        AddLog("VideoModel", "Yerel kurulum durumu kontrol ediliyor.");
        var inventory = await _inventoryService.GetInventoryAsync(models, forceRefresh, cancellationToken);
        var merged = models.Select(model =>
        {
            inventory.TryGetValue(model.ModelType, out var item);
            var installed = ResolveInstallStatus(model, item);
            var architecture = string.IsNullOrWhiteSpace(model.Architecture) ? model.BaseModelType : model.Architecture;
            return new WanGpVideoModelOptionViewModel
            {
                ModelType = model.ModelType,
                DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelType : model.DisplayName,
                Family = model.Family,
                Architecture = architecture,
                Availability = string.IsNullOrWhiteSpace(model.Availability) ? installed.ToString() : model.Availability,
                InstallationStatus = installed,
                CheckpointPath = item?.CheckpointPath,
                SupportsImageToVideo = model.SupportsImageToVideo,
                SupportsStartImage = model.SupportsStartImage || model.Inputs.Contains("image", StringComparison.OrdinalIgnoreCase),
                SupportsReferenceImage = model.SupportsReferenceImage || model.Inputs.Contains("image", StringComparison.OrdinalIgnoreCase),
                Configurations = BuildVideoConfigurations(model, item?.CheckpointPath)
            };
        })
        .Where(model => !model.IsImageOnly)
        .OrderByDescending(model => model.ModelType.Contains("ltx2_22B_distilled_gguf_q4_k_m", StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(model => model.ModelType.Contains("ltx", StringComparison.OrdinalIgnoreCase))
        .ThenBy(model => model.DisplayName)
        .ToList();

        var installedModels = merged.Where(model => model.IsSelectable).ToList();
        var otherModels = merged.Where(model => !model.IsSelectable).ToList();
        var current = SelectedVideoModel?.ModelType;

        void ApplyModels()
        {
            InstalledVideoModels.Clear();
            OtherVideoModels.Clear();
            foreach (var model in installedModels)
            {
                InstalledVideoModels.Add(model);
            }

            foreach (var model in otherModels)
            {
                OtherVideoModels.Add(model);
            }

            SelectedVideoModel = InstalledVideoModels.FirstOrDefault(model => model.ModelType == current)
                ?? InstalledVideoModels.FirstOrDefault(model => model.ModelType.Contains("ltx2_22B_distilled_gguf_q4_k_m", StringComparison.OrdinalIgnoreCase))
                ?? InstalledVideoModels.FirstOrDefault(model => model.ModelType.Contains("ltx2_22B_distilled", StringComparison.OrdinalIgnoreCase))
                ?? InstalledVideoModels.FirstOrDefault();
        }

        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            await Application.Current.Dispatcher.InvokeAsync(ApplyModels);
        }
        else
        {
            ApplyModels();
        }

        _activityCenter.Snapshot.ModelDiscoveryStatus = $"MCP={models.Count}; Installed={InstalledVideoModels.Count}; Other={OtherVideoModels.Count}";
        VideoStatus = InstalledVideoModels.Count == 0
            ? $"Video modelleri bulundu ({models.Count}) fakat kurulu I2V checkpoint eslesmesi yapilamadi. Diagnostic: {Path.Combine(Path.GetTempPath(), "DirectorWanGpVideoDiagnostics")}" 
            : $"{InstalledVideoModels.Count} kurulu video modeli hazir. Secili model: {SelectedVideoModel?.DisplayText ?? "-"}";
        ModelStatus = $"Gorsel={InstalledModels.Count}; Video={InstalledVideoModels.Count}";
        AddLog("VideoModel", $"Local registry'den {inventory.Count} model durumu okundu.");
        AddLog("VideoModel", $"{InstalledVideoModels.Count} kurulu video modeli bulundu; UI collection'a {InstalledVideoModels.Count} model eklendi.", InstalledVideoModels.Count > 0 ? GenerationLogLevel.Success : GenerationLogLevel.Warning);
        RaiseCommandStates();
    }

    private static WanGpModelInstallStatus ResolveInstallStatus(WanGpModelInfo model, WanGpLocalModelInventoryItem? item)
    {
        if (item?.Status == WanGpModelInstallStatus.Installed)
        {
            return WanGpModelInstallStatus.Installed;
        }

        if (model.IsAvailable)
        {
            return WanGpModelInstallStatus.Installed;
        }

        return item?.Status ?? WanGpModelInstallStatus.Unknown;
    }
    private void LoadVideoConfigurations(WanGpVideoModelOptionViewModel? model)
    {
        VideoConfigurations.Clear();
        if (model is null)
        {
            SelectedVideoConfiguration = null;
            return;
        }

        foreach (var configuration in model.Configurations.Where(configuration => configuration.IsAvailable))
        {
            VideoConfigurations.Add(configuration);
        }

        SelectedVideoConfiguration = VideoConfigurations.FirstOrDefault(item =>
                item.DisplayText.Contains("GGUF", StringComparison.OrdinalIgnoreCase) &&
                item.DisplayText.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase) &&
                item.DisplayText.Contains("Light", StringComparison.OrdinalIgnoreCase))
            ?? VideoConfigurations.FirstOrDefault();
    }

    private static List<WanGpVideoConfigurationOptionViewModel> BuildVideoConfigurations(WanGpModelInfo model, string? checkpointPath)
    {
        var configurations = new List<WanGpVideoConfigurationOptionViewModel>();
        var raw = model.RawMetadata.ToJsonString();
        if (raw.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase) || raw.Contains("GGUF", StringComparison.OrdinalIgnoreCase))
        {
            configurations.Add(new WanGpVideoConfigurationOptionViewModel
            {
                Key = "default",
                DisplayName = raw.Contains("Light", StringComparison.OrdinalIgnoreCase) ? "GGUF Q4_K_M Light" : "GGUF Q4_K_M",
                Quantization = "Q4_K_M",
                CheckpointPath = checkpointPath,
                IsAvailable = !string.IsNullOrWhiteSpace(checkpointPath),
                SettingsPatch = new Dictionary<string, object?>()
            });
        }

        if (configurations.Count == 0)
        {
            configurations.Add(new WanGpVideoConfigurationOptionViewModel
            {
                Key = "default",
                DisplayName = "Varsayilan model yapilandirmasi",
                CheckpointPath = checkpointPath,
                IsAvailable = !string.IsNullOrWhiteSpace(checkpointPath),
                SettingsPatch = new Dictionary<string, object?>()
            });
        }

        return configurations;
    }

    private async Task PrepareVideoPromptAsync()
    {
        if (SelectedScene is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _activityCenter.StartOperation("VideoPromptPreparing", FilmProjectId, ProjectName, SelectedScene.Id, SelectedScene.SceneNumber);
            AddLog("VideoPromptPreparing", $"Sahne {SelectedScene.SceneNumber} video promptu hazirlaniyor.");
            var request = await _videoPromptComposerService.BuildRequestAsync(SelectedScene.Id);
            var result = await _videoPromptComposerService.ComposeAsync(request);
            PreparedVideoPrompt = result.VideoPrompt;
            PreparedVideoNegativePrompt = string.IsNullOrWhiteSpace(result.VideoNegativePrompt)
                ? BuildDefaultVideoNegativePrompt()
                : result.VideoNegativePrompt;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PreparedVideoPrompt))).ToLowerInvariant();
            AddLog("VideoPromptPreparing", $"Video hareket promptu hazirlandi. Uzunluk={PreparedVideoPrompt.Length}, SHA256={hash[..12]}", GenerationLogLevel.Success);
            AddLog("OllamaModelUnloading", "Qwen modeli GPU belleginden cikariliyor.");
            await _ollamaModelLifecycleService.UnloadModelAsync("qwen3-vl:30b-a3b-instruct");
            var unloaded = await _ollamaModelLifecycleService.WaitUntilUnloadedAsync("qwen3-vl:30b-a3b-instruct", TimeSpan.FromSeconds(45));
            if (!unloaded)
            {
                throw new InvalidOperationException("Qwen modeli bellekten cikarilamadi; WanGP video uretimi baslatilmayacak.");
            }

            _activityCenter.CompleteOperation(GenerationJobStatus.Completed, "Qwen video promptu hazirlandi ve model unload edildi.");
        }
        catch (Exception ex)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Failed, ex.Message);
            AddLog("Hata", ex.Message, GenerationLogLevel.Error);
            throw;
        }
        finally
        {
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task UpdateSceneVideoPromptAsync()
    {
        if (SelectedScene is null)
        {
            return;
        }

        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var scene = await db.FilmScenes.FirstAsync(item => item.Id == SelectedScene.Id);
        scene.VideoPrompt = PreparedVideoPrompt;
        scene.VideoNegativePrompt = PreparedVideoNegativePrompt;
        scene.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        await LoadScenesAsync();
        AddLog("VideoPrompt", "Sahne video promptu guncellendi.", GenerationLogLevel.Success);
    }

    private async Task GenerateVideoAsync()
    {
        if (SelectedScene is null || SelectedVideoModel is null)
        {
            return;
        }

        var sourceImage = SelectedScene.Assets.FirstOrDefault(asset => asset.IsSelected) ?? SelectedScene.Assets.FirstOrDefault();
        if (sourceImage is null || !File.Exists(sourceImage.FilePath))
        {
            AddLog("Video", "Once bu sahne icin ana referans gorsel secin.", GenerationLogLevel.Warning);
            return;
        }

        IsBusy = true;
        _generationCancellation = new CancellationTokenSource();
        var sceneNumber = SelectedScene.SceneNumber;
        try
        {
            _activityCenter.StartOperation("VideoGenerating", FilmProjectId, ProjectName, SelectedScene.Id, SelectedScene.SceneNumber);
            var request = new WanGpVideoGenerationRequest
            {
                FilmProjectId = FilmProjectId,
                SceneId = SelectedScene.Id,
                SourceImageAssetId = sourceImage.Id,
                SourceImagePath = sourceImage.FilePath,
                ModelType = SelectedVideoModel.ModelType,
                Prompt = string.IsNullOrWhiteSpace(PreparedVideoPrompt) ? SelectedScene.VideoPrompt : PreparedVideoPrompt,
                NegativePrompt = string.IsNullOrWhiteSpace(PreparedVideoNegativePrompt) ? SelectedScene.VideoNegativePrompt : PreparedVideoNegativePrompt,
                Resolution = SelectedVideoResolution,
                DurationSeconds = Math.Max(1, SelectedScene.DurationSeconds),
                InferenceSteps = VideoInferenceSteps,
                Seed = VideoSeed,
                RandomSeed = VideoRandomSeed,
                SettingsPatch = SelectedVideoConfiguration?.SettingsPatch ?? new Dictionary<string, object?>()
            };
            AddLog("Video", $"Command=GenerateVideo; Service=VideoGenerationService; MediaType=Video; Model={SelectedVideoModel.ModelType}; Config={SelectedVideoConfiguration?.DisplayText ?? "-"}; SourceImageAssetId={sourceImage.Id}; SceneId={SelectedScene.Id}; ProjectId={FilmProjectId}");
            var progress = new Progress<MediaGenerationProgress>(OnProgressChanged);
            await _videoGenerationService.GenerateSceneVideoAsync(request, progress, _generationCancellation.Token);
            _activityCenter.CompleteOperation(GenerationJobStatus.Completed, "Video uretimi tamamlandi.");
            await LoadScenesAsync();
            SelectedScene = Scenes.FirstOrDefault(scene => scene.SceneNumber == sceneNumber);
        }
        catch (OperationCanceledException)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Cancelled, "Video uretimi iptal edildi.");
            AddLog("Iptal", "Video uretimi iptal edildi.", GenerationLogLevel.Warning);
        }
        catch (Exception ex)
        {
            _activityCenter.CompleteOperation(GenerationJobStatus.Failed, ex.Message);
            AddLog("Hata", ex.Message, GenerationLogLevel.Error);
            throw;
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            IsBusy = false;
            RaiseCommandStates();
        }
    }

    private async Task SelectVideoAssetAsync()
    {
        if (SelectedVideoAsset is null)
        {
            return;
        }

        await _videoGenerationService.SetSelectedVideoAssetAsync(SelectedVideoAsset.Id);
        await LoadScenesAsync();
        AddLog("Video", "Secili video guncellendi.", GenerationLogLevel.Success);
    }

    private bool CanPrepareVideoPrompt()
    {
        return !IsBusy && SelectedScene?.Assets.Any(asset => asset.IsSelected || File.Exists(asset.FilePath)) == true;
    }

    private bool CanGenerateVideo()
    {
        var sourceImage = SelectedScene?.Assets.FirstOrDefault(asset => asset.IsSelected) ?? SelectedScene?.Assets.FirstOrDefault();
        return !IsBusy &&
            !_gpuCoordinator.IsBusy &&
            !_activityCenter.Snapshot.HasActiveOperation &&
            _activityCenter.Snapshot.McpState == WanGpMcpConnectionState.Connected &&
            SelectedVideoModel is { IsSelectable: true } &&
            sourceImage is not null &&
            File.Exists(sourceImage.FilePath) &&
            !string.IsNullOrWhiteSpace(string.IsNullOrWhiteSpace(PreparedVideoPrompt) ? SelectedScene?.VideoPrompt : PreparedVideoPrompt);
    }

    private static string BuildDefaultVideoNegativePrompt()
    {
        return "identity drift, face distortion, extra limbs, missing fingers, duplicated subjects, warped anatomy, clothing changes, background morphing, camera jitter, flicker, frame interpolation artifacts, sudden cuts, text, subtitles, watermark, logo";
    }

    private WanGpImageGenerationRequest BuildRequest(ProductionSceneRowViewModel? scene)
    {
        return new WanGpImageGenerationRequest
        {
            ModelType = SelectedModelType,
            Prompt = scene?.ImagePrompt ?? string.Empty,
            NegativePrompt = scene?.ImageNegativePrompt ?? string.Empty,
            Resolution = SelectedResolution,
            InferenceSteps = InferenceSteps,
            Seed = Seed,
            RandomSeed = RandomSeed,
            StopOnError = false
        };
    }

    private void OnProgressChanged(MediaGenerationProgress progress)
    {
        ProgressPercentage = progress.OverallProgress > 0 ? progress.OverallProgress : progress.SceneProgress;
        _activityCenter.UpdateProgress(ProgressPercentage, progress.Phase, progress.CurrentStep, progress.TotalSteps);
        AddLog(progress.Phase, progress.Message, progress.Level);
        OnPropertyChanged(nameof(ActivityStep));
    }

    private void AddLog(string phase, string message, GenerationLogLevel level = GenerationLogLevel.Information)
    {
        _activityCenter.AddLog(phase, message, level);
    }

    private bool CanGenerate()
    {
        return !IsBusy &&
            !_activityCenter.Snapshot.HasActiveOperation &&
            _activityCenter.Snapshot.McpState == WanGpMcpConnectionState.Connected &&
            SelectedModel is { IsSelectable: true } &&
            SelectedScene is { ImagePrompt.Length: > 0 };
    }

    private void OpenSelectedFolder()
    {
        var root = _options.GetEffectiveOutputRootPath();
        var path = SelectedScene is null
            ? root
            : Path.Combine(root, FilmProjectId.ToString(), "scenes", SelectedScene.SceneNumber.ToString("000"), "images");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenWanGpOutputFolder()
    {
        var path = _options.GetEffectiveOutputDirectory();
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void OpenGui()
    {
        Process.Start(new ProcessStartInfo { FileName = _options.GuiUrl, UseShellExecute = true });
    }

    private void RaiseCommandStates()
    {
        if (TestConnectionCommand is AsyncRelayCommand testCommand) testCommand.RaiseCanExecuteChanged();
        if (RefreshModelsCommand is AsyncRelayCommand refreshCommand) refreshCommand.RaiseCanExecuteChanged();
        if (ReconnectCommand is AsyncRelayCommand reconnectCommand) reconnectCommand.RaiseCanExecuteChanged();
        if (GenerateSelectedCommand is AsyncRelayCommand generateCommand) generateCommand.RaiseCanExecuteChanged();
        if (GenerateMissingCommand is AsyncRelayCommand missingCommand) missingCommand.RaiseCanExecuteChanged();
        if (CancelCommand is AsyncRelayCommand cancelCommand) cancelCommand.RaiseCanExecuteChanged();
        if (OpenFolderCommand is RelayCommand openCommand) openCommand.RaiseCanExecuteChanged();
        if (OpenDirectorImagesFolderCommand is RelayCommand directorOpenCommand) directorOpenCommand.RaiseCanExecuteChanged();
        if (SelectAssetCommand is AsyncRelayCommand selectCommand) selectCommand.RaiseCanExecuteChanged();
        if (ScanWanGpOutputsCommand is AsyncRelayCommand scanCommand) scanCommand.RaiseCanExecuteChanged();
        if (ImportOutputCommand is AsyncRelayCommand importCommand) importCommand.RaiseCanExecuteChanged();
        if (PrepareVideoPromptCommand is AsyncRelayCommand prepareVideoCommand) prepareVideoCommand.RaiseCanExecuteChanged();
        if (ResetPreparedVideoPromptCommand is RelayCommand resetVideoCommand) resetVideoCommand.RaiseCanExecuteChanged();
        if (UpdateSceneVideoPromptCommand is AsyncRelayCommand updateVideoCommand) updateVideoCommand.RaiseCanExecuteChanged();
        if (GenerateVideoCommand is AsyncRelayCommand generateVideoCommand) generateVideoCommand.RaiseCanExecuteChanged();
        if (SelectVideoAssetCommand is AsyncRelayCommand selectVideoCommand) selectVideoCommand.RaiseCanExecuteChanged();
        if (RefreshVideoModelsCommand is AsyncRelayCommand refreshVideoModelsCommand) refreshVideoModelsCommand.RaiseCanExecuteChanged();
    }

    private void RefreshPreviewImage()
    {
        PreviewImageSource = null;
        OnPropertyChanged(nameof(HasPreview));
        var path = SelectedAsset?.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = PreviewPath;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            PreviewStatus = "Secili gorsel burada goruntulenecek.";
            return;
        }

        if (!File.Exists(path))
        {
            PreviewStatus = "Preview dosyasi bulunamadi.";
            AddLog("Preview", $"Dosya bulunamadi: {path}", GenerationLogLevel.Warning);
            return;
        }

        try
        {
            PreviewImageSource = LoadPreviewImage(path);
            PreviewStatus = string.Empty;
            OnPropertyChanged(nameof(HasPreview));
        }
        catch (Exception ex)
        {
            PreviewStatus = "Preview BitmapImage olusturulamadi.";
            AddLog("Preview", ex.Message, GenerationLogLevel.Warning);
        }
    }

    private static BitmapSource LoadPreviewImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

public sealed class WanGpModelOptionViewModel
{
    public string ModelType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayText => $"{(string.IsNullOrWhiteSpace(DisplayName) ? ModelType : DisplayName)} - {ModelType}";
    public string Family { get; set; } = string.Empty;
    public string MainOutput { get; set; } = string.Empty;
    public string Inputs { get; set; } = string.Empty;
    public WanGpModelInstallStatus InstallationStatus { get; set; } = WanGpModelInstallStatus.Unknown;
    public string? CheckpointPath { get; set; }
    public bool SupportsTextToImage { get; set; }
    public bool SupportsReferenceImage { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public bool IsInstalled => InstallationStatus == WanGpModelInstallStatus.Installed;
    public bool IsSelectable => IsInstalled;
    public string Summary => DisplayText;
}

public sealed class WanGpVideoModelOptionViewModel
{
    public string ModelType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayText => $"{(string.IsNullOrWhiteSpace(DisplayName) ? ModelType : DisplayName)} - {ModelType}";
    public string Architecture { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string Availability { get; set; } = string.Empty;
    public WanGpModelInstallStatus InstallationStatus { get; set; } = WanGpModelInstallStatus.Unknown;
    public string? CheckpointPath { get; set; }
    public bool SupportsImageToVideo { get; set; }
    public bool SupportsStartImage { get; set; }
    public bool SupportsReferenceImage { get; set; }
    public bool SupportsEndImage { get; set; }
    public bool SupportsNegativePrompt { get; set; }
    public bool SupportsAudioOutput { get; set; }
    public bool SupportsDurationSeconds { get; set; }
    public bool SupportsFrameCount { get; set; }
    public bool SupportsFps { get; set; }
    public List<WanGpVideoConfigurationOptionViewModel> Configurations { get; set; } = [];
    public bool IsInstalled => InstallationStatus == WanGpModelInstallStatus.Installed;
    public bool IsSelectable => IsInstalled && SupportsImageToVideo && (SupportsStartImage || SupportsReferenceImage) && !IsImageOnly;
    public bool IsImageOnly => ModelType.Contains("qwen_image", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("qwen image", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("flux", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("krea", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("z-image", StringComparison.OrdinalIgnoreCase);
}

public sealed class WanGpVideoConfigurationOptionViewModel
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DisplayText => string.IsNullOrWhiteSpace(Quantization) ? DisplayName : $"{DisplayName} - {Quantization}";
    public string Quantization { get; set; } = string.Empty;
    public string? CheckpointPath { get; set; }
    public bool IsAvailable { get; set; }
    public Dictionary<string, object?> SettingsPatch { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProductionSceneRowViewModel
{
    public int Id { get; set; }
    public int SceneNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ImagePrompt { get; set; } = string.Empty;
    public string ImageNegativePrompt { get; set; } = string.Empty;
    public string? SelectedImagePath { get; set; }
    public string VideoPrompt { get; set; } = string.Empty;
    public string VideoNegativePrompt { get; set; } = string.Empty;
    public int DurationSeconds { get; set; }
    public int ImageCount { get; set; }
    public string LastStatus { get; set; } = string.Empty;
    public List<SceneMediaAssetRowViewModel> Assets { get; set; } = [];
    public List<SceneMediaAssetRowViewModel> VideoAssets { get; set; } = [];
}

public sealed class SceneMediaAssetRowViewModel
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? ThumbnailPath { get; set; }
    public int VersionNumber { get; set; }
    public bool IsSelected { get; set; }
    public string ModelType { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int? Seed { get; set; }
    public double? DurationSeconds { get; set; }
}

