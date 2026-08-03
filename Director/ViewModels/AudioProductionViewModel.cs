using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Director.Commands;
using Director.Helpers;
using Director.Models;
using Director.Services.Interfaces;
using Director.WanGp;

namespace Director.ViewModels;

public sealed class AudioProductionViewModel : ObservableObject
{
    private readonly IAudioGenerationService _audioGenerationService;
    private int _filmProjectId;
    private int? _sceneId;
    private int? _selectedSegmentId;
    private bool _isBusy;
    private string _status = "Konusma uretimi hazir degil.";
    private string _modelDetails = "KugelAudio modeli henuz dogrulanmadi.";
    private Uri? _segmentPreviewUri;
    private Uri? _speechTrackPreviewUri;
    private Uri? _finalDialogueVideoPreviewUri;

    public AudioProductionViewModel(IAudioGenerationService audioGenerationService)
    {
        _audioGenerationService = audioGenerationService;
        VoicePresets = new ObservableCollection<WanGpVoicePreset>();
        Segments = new ObservableCollection<AudioSpeechSegmentRowViewModel>();
        RefreshAudioModelCommand = new AsyncRelayCommand(RefreshAudioModelAsync, () => !IsBusy);
        PrepareSpeechPlanCommand = new AsyncRelayCommand(PrepareSpeechPlanAsync, () => !IsBusy && SceneId is not null);
        GenerateSelectedSegmentCommand = new AsyncRelayCommand(GenerateSelectedSegmentAsync, () => !IsBusy && SelectedSegmentId is not null);
        GenerateAllSegmentsCommand = new AsyncRelayCommand(GenerateAllSegmentsAsync, () => !IsBusy && Segments.Count > 0);
        CreateSpeechTrackCommand = new AsyncRelayCommand(CreateSpeechTrackAsync, () => !IsBusy && SceneId is not null && Segments.Count > 0);
        CreateFinalDialogueVideoCommand = new AsyncRelayCommand(CreateFinalDialogueVideoAsync, () => !IsBusy && SceneId is not null);
    }

    public ObservableCollection<WanGpVoicePreset> VoicePresets { get; }
    public ObservableCollection<AudioSpeechSegmentRowViewModel> Segments { get; }
    public ICommand RefreshAudioModelCommand { get; }
    public ICommand PrepareSpeechPlanCommand { get; }
    public ICommand GenerateSelectedSegmentCommand { get; }
    public ICommand GenerateAllSegmentsCommand { get; }
    public ICommand CreateSpeechTrackCommand { get; }
    public ICommand CreateFinalDialogueVideoCommand { get; }

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

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ModelDetails { get => _modelDetails; private set => SetProperty(ref _modelDetails, value); }
    public Uri? SegmentPreviewUri { get => _segmentPreviewUri; private set => SetProperty(ref _segmentPreviewUri, value); }
    public Uri? SpeechTrackPreviewUri { get => _speechTrackPreviewUri; private set => SetProperty(ref _speechTrackPreviewUri, value); }
    public Uri? FinalDialogueVideoPreviewUri { get => _finalDialogueVideoPreviewUri; private set => SetProperty(ref _finalDialogueVideoPreviewUri, value); }

    public int? SceneId
    {
        get => _sceneId;
        private set
        {
            if (SetProperty(ref _sceneId, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public int? SelectedSegmentId
    {
        get => _selectedSegmentId;
        set
        {
            if (SetProperty(ref _selectedSegmentId, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public void SetContext(int filmProjectId, int? sceneId)
    {
        _filmProjectId = filmProjectId;
        SceneId = sceneId;
    }

    private async Task RefreshAudioModelAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _audioGenerationService.DiscoverKugelAudioAsync(forceRefresh: true);
            VoicePresets.Clear();
            foreach (var voice in result.Contract?.AvailableVoices ?? [])
            {
                VoicePresets.Add(voice);
            }

            ModelDetails = result.Model is null
                ? result.Message
                : $"Model: {result.Model.DisplayName}\nmodel_type: {result.Model.ModelType}\nDurum: {result.Inventory?.Status}\nVoice preset: {VoicePresets.Count}\nRaw voice cloning: {(result.Contract?.SupportsRawReferenceAudio == true ? "Schema destekliyor" : "Desteklenmiyor")}";
            Status = result.Message;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PrepareSpeechPlanAsync()
    {
        if (SceneId is not int sceneId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var plan = await _audioGenerationService.CreateBasicSpeechPlanAsync(sceneId);
            LoadSegments(plan);
            Status = Segments.Count == 0
                ? "Bu sahnede konusma bulunmuyor."
                : $"Sahne konusma plani hazir. Replik={Segments.Count}; Hedef={plan.TargetDurationSeconds} sn.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GenerateSelectedSegmentAsync()
    {
        if (SelectedSegmentId is int id)
        {
            await GenerateSegmentAsync(id);
        }
    }

    private async Task GenerateAllSegmentsAsync()
    {
        foreach (var segment in Segments.ToList())
        {
            await GenerateSegmentAsync(segment.Id);
        }
    }

    private async Task CreateSpeechTrackAsync()
    {
        if (SceneId is not int sceneId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var asset = await _audioGenerationService.CreateSpeechTrackForSceneAsync(sceneId);
            SpeechTrackPreviewUri = File.Exists(asset.FilePath) ? new Uri(Path.GetFullPath(asset.FilePath), UriKind.Absolute) : null;
            Status = $"Konusma kanali hazir: {Path.GetFileName(asset.FilePath)}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CreateFinalDialogueVideoAsync()
    {
        if (SceneId is not int sceneId)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var asset = await _audioGenerationService.CreateFinalDialogueVideoForSceneAsync(sceneId);
            FinalDialogueVideoPreviewUri = File.Exists(asset.FilePath) ? new Uri(Path.GetFullPath(asset.FilePath), UriKind.Absolute) : null;
            Status = $"Final konusmali video hazir: {Path.GetFileName(asset.FilePath)}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GenerateSegmentAsync(int id)
    {
        IsBusy = true;
        try
        {
            var asset = await _audioGenerationService.GenerateSpeechSegmentAsync(id);
            SegmentPreviewUri = File.Exists(asset.FilePath) ? new Uri(Path.GetFullPath(asset.FilePath), UriKind.Absolute) : null;
            Status = $"Replik sesi hazir: {Path.GetFileName(asset.FilePath)}";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadSegments(SceneSpeechPlan plan)
    {
        Segments.Clear();
        foreach (var segment in plan.Segments.OrderBy(item => item.SortOrder))
        {
            Segments.Add(new AudioSpeechSegmentRowViewModel
            {
                Id = segment.Id,
                SpeakerKey = segment.SpeakerKey,
                SourceText = segment.SourceText,
                SpokenText = segment.TurkishText,
                Emotion = segment.Emotion,
                StartTimeSeconds = segment.StartTimeSeconds,
                TargetDurationSeconds = segment.TargetDurationSeconds,
                ActualDurationSeconds = segment.ActualDurationSeconds,
                Status = segment.Status.ToString(),
                TextHash = ComputeHash(segment.TurkishText)[..12]
            });
        }
    }

    private void RaiseCommandStates()
    {
        (RefreshAudioModelCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrepareSpeechPlanCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (GenerateSelectedSegmentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (GenerateAllSegmentsCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateSpeechTrackCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (CreateFinalDialogueVideoCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string ComputeHash(string value)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public sealed class AudioSpeechSegmentRowViewModel
{
    public int Id { get; set; }
    public string SpeakerKey { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public string SpokenText { get; set; } = string.Empty;
    public string Emotion { get; set; } = string.Empty;
    public double StartTimeSeconds { get; set; }
    public double TargetDurationSeconds { get; set; }
    public double? ActualDurationSeconds { get; set; }
    public string Status { get; set; } = string.Empty;
    public string TextHash { get; set; } = string.Empty;
}
