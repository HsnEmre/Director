using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Data;
using Director.Enums;
using Director.Models;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class AudioGenerationService : IAudioGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IWanGpClient _wanGpClient;
    private readonly IWanGpLocalModelInventoryService _inventoryService;
    private readonly IWanGpAudioInputContractResolver _contractResolver;
    private readonly IWanGpAudioRequestBuilder _requestBuilder;
    private readonly IWanGpAudioOutputResolver _outputResolver;
    private readonly IVideoMetadataService _metadataService;
    private readonly IMediaFileService _mediaFileService;
    private readonly ISpeechTimelineMixingService _speechTimelineMixingService;
    private readonly IFinalDialogueVideoMuxingService _finalDialogueVideoMuxingService;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IApplicationActivityCenter _activityCenter;
    private readonly ILogger<AudioGenerationService> _logger;

    public AudioGenerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IWanGpClient wanGpClient,
        IWanGpLocalModelInventoryService inventoryService,
        IWanGpAudioInputContractResolver contractResolver,
        IWanGpAudioRequestBuilder requestBuilder,
        IWanGpAudioOutputResolver outputResolver,
        IVideoMetadataService metadataService,
        IMediaFileService mediaFileService,
        ISpeechTimelineMixingService speechTimelineMixingService,
        IFinalDialogueVideoMuxingService finalDialogueVideoMuxingService,
        IGpuGenerationCoordinator gpuCoordinator,
        IApplicationActivityCenter activityCenter,
        ILogger<AudioGenerationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _wanGpClient = wanGpClient;
        _inventoryService = inventoryService;
        _contractResolver = contractResolver;
        _requestBuilder = requestBuilder;
        _outputResolver = outputResolver;
        _metadataService = metadataService;
        _mediaFileService = mediaFileService;
        _speechTimelineMixingService = speechTimelineMixingService;
        _finalDialogueVideoMuxingService = finalDialogueVideoMuxingService;
        _gpuCoordinator = gpuCoordinator;
        _activityCenter = activityCenter;
        _logger = logger;
    }

    public async Task<AudioModelDiscoveryResult> DiscoverKugelAudioAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        _activityCenter.AddLog("AudioModelDiscovering", "KugelAudio modeli dogrulaniyor.");
        var models = await _wanGpClient.GetAvailableAudioModelsAsync(cancellationToken);
        var model = models.FirstOrDefault(item =>
            item.DisplayName.Contains("KugelAudio", StringComparison.OrdinalIgnoreCase) ||
            item.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return new AudioModelDiscoveryResult { Message = "KugelAudio modeli WanGP MCP listesinde bulunamadi." };
        }

        var inventory = await _inventoryService.GetInventoryAsync([model], forceRefresh, cancellationToken);
        inventory.TryGetValue(model.ModelType, out var item);
        var schema = await _wanGpClient.GetModelSchemaAsync(model.ModelType, cancellationToken);
        var contract = schema is null ? null : _contractResolver.Resolve(model, schema);
        var result = new AudioModelDiscoveryResult
        {
            Model = model,
            Inventory = item,
            Schema = schema,
            Contract = contract,
            Message = item?.Status == WanGpModelInstallStatus.Installed
                ? $"{contract?.AvailableVoices.Count ?? 0} kullanilabilir ses preset'i bulundu."
                : "KugelAudio modeli WanGP'de listeleniyor ancak gerekli dosyalar kurulu degil."
        };

        _activityCenter.AddLog("AudioModelDiscovering", result.Message, result.IsSelectable ? GenerationLogLevel.Success : GenerationLogLevel.Warning);
        return result;
    }

    public async Task<SceneSpeechPlan> CreateBasicSpeechPlanAsync(int sceneId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.FilmScenes
            .Include(item => item.FilmProject)
            .Include(item => item.FilmStory)
            .ThenInclude(story => story.Characters)
            .FirstAsync(item => item.Id == sceneId, cancellationToken);
        var targetDuration = scene.FilmProject.ClipDurationSeconds > 0 ? scene.FilmProject.ClipDurationSeconds : Math.Max(1, scene.DurationSeconds);
        var dialogueLines = SpeechDialogueExtractor.Extract(scene.DialogueJson, scene.FilmStory.Characters.OrderBy(item => item.SortOrder).ToList());

        var plan = await db.SceneSpeechPlans
            .Include(item => item.Segments)
            .FirstOrDefaultAsync(item => item.SceneId == sceneId, cancellationToken);
        if (plan is null)
        {
            plan = new SceneSpeechPlan
            {
                FilmProjectId = scene.FilmProjectId,
                SceneId = scene.Id,
                TargetDurationSeconds = targetDuration,
                Status = SpeechPlanStatus.Prepared,
                CreatedAt = DateTime.Now
            };
            db.SceneSpeechPlans.Add(plan);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            db.SceneSpeechSegments.RemoveRange(plan.Segments);
            plan.TargetDurationSeconds = targetDuration;
            plan.Status = SpeechPlanStatus.Prepared;
            plan.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (dialogueLines.Count == 0)
        {
            plan.Status = SpeechPlanStatus.Prepared;
            plan.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
            _activityCenter.AddLog("SpeechPlanPreparing", $"Sahne {scene.SceneNumber} icinde konusma bulunmuyor.", GenerationLogLevel.Information);
            return await db.SceneSpeechPlans.Include(item => item.Segments).AsNoTracking().FirstAsync(item => item.Id == plan.Id, cancellationToken);
        }

        var discovery = await DiscoverKugelAudioAsync(cancellationToken: cancellationToken);
        if (!discovery.IsSelectable || discovery.Model is null || discovery.Contract is null)
        {
            throw new InvalidOperationException(discovery.Message);
        }

        _activityCenter.AddLog("SpeechPlanPreparing", $"Sahne {scene.SceneNumber} icindeki {dialogueLines.Count} konusma bulundu.");
        var cursor = 0.2d;
        var availableDuration = Math.Max(0.5, targetDuration - 0.4);
        var perLineDuration = Math.Max(0.8, Math.Min(3.2, availableDuration / dialogueLines.Count));
        foreach (var line in dialogueLines)
        {
            var character = scene.FilmStory.Characters.First(item => item.Id == line.StoryCharacterId);
            var profile = await EnsureCharacterProfileAsync(db, scene, character, discovery, cancellationToken);
            db.SceneSpeechSegments.Add(new SceneSpeechSegment
            {
                SceneSpeechPlanId = plan.Id,
                SpeakerType = SpeechSpeakerType.Character,
                StoryCharacterId = line.StoryCharacterId,
                SpeakerKey = line.SpeakerKey,
                SourceText = line.SourceText,
                TurkishText = FitSpokenText(line.SpokenText, perLineDuration),
                Emotion = line.Emotion,
                StartTimeSeconds = cursor,
                TargetDurationSeconds = perLineDuration,
                VoiceProfileId = profile.Id,
                SortOrder = line.SortOrder,
                Status = SpeechSegmentStatus.Pending,
                CreatedAt = DateTime.Now
            });
            cursor += perLineDuration + 0.15;
        }

        await db.SaveChangesAsync(cancellationToken);
        _activityCenter.AddLog("SpeechPlanPreparing", $"Sahne {scene.SceneNumber} konusma plani DialogueJson kaynakli hazirlandi.", GenerationLogLevel.Success);
        return await db.SceneSpeechPlans.Include(item => item.Segments).AsNoTracking().FirstAsync(item => item.Id == plan.Id, cancellationToken);
    }

    public async Task<SceneMediaAsset> GenerateSpeechSegmentAsync(int speechSegmentId, CancellationToken cancellationToken = default)
    {
        SceneSpeechSegment segment;
        FilmScene scene;
        CharacterVoiceProfile profile;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            segment = await db.SceneSpeechSegments.AsNoTracking().FirstAsync(item => item.Id == speechSegmentId, cancellationToken);
            var plan = await db.SceneSpeechPlans.AsNoTracking().FirstAsync(item => item.Id == segment.SceneSpeechPlanId, cancellationToken);
            scene = await db.FilmScenes.AsNoTracking().FirstAsync(item => item.Id == plan.SceneId, cancellationToken);
            profile = await db.CharacterVoiceProfiles.AsNoTracking().FirstAsync(item => item.Id == segment.VoiceProfileId, cancellationToken);
        }

        var discovery = await DiscoverKugelAudioAsync(cancellationToken: cancellationToken);
        if (!discovery.IsSelectable || discovery.Contract is null)
        {
            throw new InvalidOperationException(discovery.Message);
        }

        var before = _outputResolver.CaptureSnapshot();
        var request = new WanGpAudioGenerationRequest
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            SpeechSegmentId = segment.Id,
            ModelType = profile.ModelType,
            TurkishText = segment.TurkishText,
            VoicePresetKey = profile.VoicePresetKey,
            Language = profile.Language,
            Emotion = segment.Emotion,
            CfgScale = profile.CfgScale,
            Seed = profile.Seed,
            DoSample = profile.DoSample,
            Temperature = profile.Temperature,
            MaxNewTokens = profile.MaxNewTokens,
            TargetDurationSeconds = segment.TargetDurationSeconds,
            InputContract = discovery.Contract
        };
        var build = await _requestBuilder.BuildAsync(request, cancellationToken);
        var job = await CreateJobAsync(scene, segment, profile, build, cancellationToken);
        WanGpJobSnapshot snapshot;
        await using (var lease = await _gpuCoordinator.AcquireAsync(
            GenerationOperationType.Audio,
            scene.FilmProjectId,
            scene.Id,
            cancellationToken))
        {
        var submission = await _wanGpClient.SubmitAudioGenerationAsync(build.Source, cancellationToken);
        await UpdateJobAsync(job.Id, existing =>
        {
            existing.ExternalJobId = submission.ExternalJobId;
            existing.Status = GenerationJobStatus.Running;
            existing.StartedAt = DateTime.Now;
            existing.CurrentPhase = "SpeechSegmentGenerating";
            existing.UpdatedAt = DateTime.Now;
        }, cancellationToken);

        snapshot = await PollUntilAudioOutputAsync(submission.ExternalJobId, before, job.StartedAt ?? job.CreatedAt, cancellationToken);
        }
        var outputPath = snapshot.GeneratedFiles.FirstOrDefault() ?? snapshot.OutputPath
            ?? throw new InvalidOperationException("WanGP audio output dosyasi bulunamadi.");
        return await SaveAudioAssetAsync(scene.Id, job.Id, segment.Id, profile.Id, outputPath, build.TextHash, cancellationToken);
    }

    public async Task<SceneMediaAsset> CreateSpeechTrackForSceneAsync(int sceneId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.SceneSpeechPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.SceneId == sceneId, cancellationToken)
            ?? throw new InvalidOperationException("Once konusma plani hazirlanmali.");
        return await _speechTimelineMixingService.CreateSpeechTrackAsync(plan.Id, cancellationToken);
    }

    public async Task<SceneMediaAsset> CreateFinalDialogueVideoForSceneAsync(int sceneId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var video = await db.SceneMediaAssets
            .AsNoTracking()
            .Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Video && item.Role != MediaAssetRole.FinalDialogueVideo)
            .OrderByDescending(item => item.IsSelected)
            .ThenByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Final mux icin kaynak video asset'i bulunamadi.");
        var speech = await db.SceneMediaAssets
            .AsNoTracking()
            .Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Audio && item.Role == MediaAssetRole.SceneSpeechTrack)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Once konusma kanali olusturulmali.");
        return await _finalDialogueVideoMuxingService.CreateFinalDialogueVideoAsync(video.Id, speech.Id, cancellationToken);
    }

    private async Task<WanGpJobSnapshot> PollUntilAudioOutputAsync(string externalJobId, WanGpOutputSnapshot before, DateTime startedAt, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            var snapshot = await _wanGpClient.GetJobAsync(externalJobId, linked.Token);
            var explicitPaths = snapshot.GeneratedFiles.ToList();
            if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
            {
                explicitPaths.Add(snapshot.OutputPath);
            }

            var output = await _outputResolver.ResolveAudioOutputsAsync(before, startedAt, explicitPaths, TimeSpan.FromSeconds(1), linked.Token);
            if (output.Success)
            {
                snapshot.Status = GenerationJobStatus.Completed;
                snapshot.GeneratedFiles = output.Candidates.Select(item => item.FilePath).ToList();
                snapshot.OutputPath = snapshot.GeneratedFiles.FirstOrDefault();
                return snapshot;
            }

            if (snapshot.Status is GenerationJobStatus.Completed or GenerationJobStatus.Failed or GenerationJobStatus.Cancelled or GenerationJobStatus.Interrupted)
            {
                return snapshot;
            }

            await Task.Delay(1000, linked.Token);
        }
    }

    private async Task<GenerationJob> CreateJobAsync(FilmScene scene, SceneSpeechSegment segment, CharacterVoiceProfile profile, WanGpAudioRequestBuildResult build, CancellationToken cancellationToken)
    {
        var settingsHash = string.IsNullOrWhiteSpace(profile.SettingsHash)
            ? AudioVoiceSettingsHasher.Compute(profile)
            : profile.SettingsHash;
        var job = new GenerationJob
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            MediaType = MediaType.Audio,
            Provider = GenerationProvider.WanGp,
            Status = GenerationJobStatus.Pending,
            ModelType = profile.ModelType,
            Prompt = $"speechSegmentId={segment.Id}; textHash={build.TextHash}",
            SettingsJson = JsonSerializer.Serialize(new
            {
                speechSegmentId = segment.Id,
                segment.StoryCharacterId,
                voiceProfileId = profile.Id,
                profile.VoicePresetKey,
                profile.Language,
                profile.CfgScale,
                profile.Seed,
                profile.DoSample,
                profile.Temperature,
                profile.MaxNewTokens,
                settingsHash,
                promptEnhancer = false,
                useEmotionStyling = profile.UseEmotionStyling,
                build.Contract.TextKey,
                build.Contract.VoiceKey,
                build.Contract.SeedKey,
                build.Contract.CfgScaleKey,
                build.Contract.DoSampleKey,
                build.Contract.TemperatureKey,
                build.Contract.MaxNewTokensKey,
                contractEvidence = build.Contract.Evidence
            }, JsonOptions),
            CurrentPhase = "AudioQueued",
            CreatedAt = DateTime.Now
        };

        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        return job;
    }

    private async Task<SceneMediaAsset> SaveAudioAssetAsync(int sceneId, int jobId, int segmentId, int voiceProfileId, string sourcePath, string textHash, CancellationToken cancellationToken)
    {
        FilmScene scene;
        GenerationJob job;
        SceneSpeechSegment segmentSnapshot;
        CharacterVoiceProfile profileSnapshot;
        int versionNumber;
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            scene = await db.FilmScenes.AsNoTracking().FirstAsync(item => item.Id == sceneId, cancellationToken);
            job = await db.GenerationJobs.AsNoTracking().FirstAsync(item => item.Id == jobId, cancellationToken);
            segmentSnapshot = await db.SceneSpeechSegments.AsNoTracking().FirstAsync(item => item.Id == segmentId, cancellationToken);
            profileSnapshot = await db.CharacterVoiceProfiles.AsNoTracking().FirstAsync(item => item.Id == voiceProfileId, cancellationToken);
            var existing = await db.SceneMediaAssets.Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Audio).ToListAsync(cancellationToken);
            versionNumber = existing.Count == 0 ? 1 : existing.Max(item => item.VersionNumber) + 1;
        }

        var metadata = await _metadataService.ProbeAsync(sourcePath, cancellationToken);
        var metadataJson = new JsonObject
        {
            ["speechSegmentId"] = segmentId,
            ["sortOrder"] = segmentSnapshot.SortOrder,
            ["speakerKey"] = segmentSnapshot.SpeakerKey,
            ["storyCharacterId"] = segmentSnapshot.StoryCharacterId,
            ["voiceProfileId"] = voiceProfileId,
            ["voicePresetKey"] = profileSnapshot.VoicePresetKey,
            ["emotion"] = segmentSnapshot.Emotion,
            ["seed"] = profileSnapshot.Seed,
            ["cfgScale"] = profileSnapshot.CfgScale,
            ["doSample"] = profileSnapshot.DoSample,
            ["temperature"] = profileSnapshot.Temperature,
            ["maxNewTokens"] = profileSnapshot.MaxNewTokens,
            ["settingsHash"] = string.IsNullOrWhiteSpace(profileSnapshot.SettingsHash) ? AudioVoiceSettingsHasher.Compute(profileSnapshot) : profileSnapshot.SettingsHash,
            ["promptEnhancer"] = false,
            ["useEmotionStyling"] = profileSnapshot.UseEmotionStyling,
            ["sourceTextHash"] = HashText(segmentSnapshot.SourceText),
            ["textHash"] = textHash,
            ["duration"] = metadata.DurationSeconds,
            ["aiGenerated"] = true,
            ["watermarkedExpectation"] = "WanGP/KugelAudio upstream behavior"
        }.ToJsonString(JsonOptions);
        var asset = await _mediaFileService.CopyGeneratedAudioAsync(scene, job, sourcePath, metadata, versionNumber, MediaAssetRole.SpeechSegment, metadataJson, cancellationToken);
        await using (var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            db.SceneMediaAssets.Add(asset);
            var segment = await db.SceneSpeechSegments.FirstAsync(item => item.Id == segmentId, cancellationToken);
            segment.ActualDurationSeconds = metadata.DurationSeconds;
            segment.Status = SpeechSegmentStatus.Completed;
            segment.UpdatedAt = DateTime.Now;
            var trackedJob = await db.GenerationJobs.FirstAsync(item => item.Id == jobId, cancellationToken);
            trackedJob.Status = GenerationJobStatus.Completed;
            trackedJob.CompletedAt = DateTime.Now;
            trackedJob.ProgressPercentage = 100;
            trackedJob.CurrentPhase = "Completed";
            trackedJob.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        _activityCenter.AddLog("AudioOutputResolving", "Ses dosyasi dogrulandi.", GenerationLogLevel.Success);
        return asset;
    }

    private async Task UpdateJobAsync(int jobId, Action<GenerationJob> update, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.GenerationJobs.FirstOrDefaultAsync(item => item.Id == jobId, cancellationToken);
        if (job is null)
        {
            return;
        }

        update(job);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<CharacterVoiceProfile> EnsureCharacterProfileAsync(AppDbContext db, FilmScene scene, StoryCharacter character, AudioModelDiscoveryResult discovery, CancellationToken cancellationToken)
    {
        var voices = discovery.Contract?.AvailableVoices ?? [];
        var voice = SelectInitialVoice(character.SortOrder, voices);
        voice ??= discovery.Contract?.AvailableVoices.FirstOrDefault()
            ?? throw new InvalidOperationException("KugelAudio voice preset bulunamadi.");
        var profile = await db.CharacterVoiceProfiles.FirstOrDefaultAsync(item =>
            item.FilmProjectId == scene.FilmProjectId &&
            item.StoryCharacterId == character.Id &&
            item.IsDefault &&
            !item.IsNarrator,
            cancellationToken);
        if (profile is not null)
        {
            var currentHash = AudioVoiceSettingsHasher.Compute(profile);
            if (!string.Equals(profile.SettingsHash, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                profile.SettingsHash = currentHash;
                profile.UpdatedAt = DateTime.Now;
                await db.SaveChangesAsync(cancellationToken);
            }

            return profile;
        }

        var seed = StableSeed(scene.FilmProjectId, character.Id, character.CharacterKey);
        profile = new CharacterVoiceProfile
        {
            FilmProjectId = scene.FilmProjectId,
            StoryCharacterId = character.Id,
            ProfileName = character.Name,
            ModelType = discovery.Model?.ModelType ?? string.Empty,
            VoicePresetKey = voice.Key,
            VoicePresetDisplayName = voice.DisplayName,
            Language = "tr",
            CfgScale = 3.0,
            Seed = seed,
            DoSample = false,
            Temperature = 1.0,
            MaxNewTokens = 64,
            SpeakingRate = 1,
            EmotionStyle = string.Empty,
            IsLocked = true,
            UseEmotionStyling = false,
            IsNarrator = false,
            IsDefault = true,
            CreatedAt = DateTime.Now
        };
        profile.SettingsHash = AudioVoiceSettingsHasher.Compute(profile);
        db.CharacterVoiceProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return profile;
    }

    private static WanGpVoicePreset? SelectInitialVoice(int sortOrder, IReadOnlyList<WanGpVoicePreset> voices)
    {
        if (voices.Count == 0)
        {
            return null;
        }

        var preferred = sortOrder switch
        {
            0 => "warm",
            1 => "clear",
            2 => "default",
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = voices.FirstOrDefault(voice => string.Equals(voice.Key, preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return voices[sortOrder % voices.Count];
    }

    private static int StableSeed(int filmProjectId, int storyCharacterId, string characterKey)
    {
        var source = $"{filmProjectId}:{storyCharacterId}:{characterKey}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Math.Abs(BitConverter.ToInt32(bytes, 0));
    }

    private static string FitSpokenText(string text, double targetDurationSeconds)
    {
        var maxWords = Math.Max(3, (int)Math.Floor(targetDurationSeconds * 2.8));
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(maxWords));
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
