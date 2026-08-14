using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Data;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class StoryGenerationService : IStoryGenerationService
{
    private const int OutlineBlockSize = 1;
    private const int ResumeSceneBatchSize = 1;
    private const int RepairExcerptMaxCharacters = 2400;
    private const int StoryBibleMinimumNumPredict = 768;
    private const int StoryBibleBriefInitialNumPredict = 1536;
    private const int StoryBibleBriefRetryNumPredict = 2048;
    private const int StoryBibleDetailedMaxNumPredict = 8192;
    private const int StoryBibleContextMarginTokens = 1024;
    public const string OpeningSceneContinuityFromPreviousScene = "Opening scene; no previous scene.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] VideoPromptAudioTerms =
    {
        "audio", "sound", "sound effects", "music", "voice", "narration", "narrator", "dialogue", "spoken", "spoken words", "ambient", "lip-sync",
        "sfx", "song", "müzik", "muzik", "ses", "diyalog", "anlatıcı", "anlatici", "konuşma", "konusma"
    };

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOllamaClient _ollamaClient;
    private readonly IStoryPromptBuilder _promptBuilder;
    private readonly IGpuGenerationCoordinator _gpuCoordinator;
    private readonly IProjectGenerationLeaseCoordinator? _projectLeaseCoordinator;
    private readonly IOllamaFailureDiagnosticWriter _failureDiagnosticWriter;
    private readonly OllamaOptions _options;
    private readonly ILogger<StoryGenerationService> _logger;

    public StoryGenerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        IStoryPromptBuilder promptBuilder,
        IGpuGenerationCoordinator gpuCoordinator,
        IProjectGenerationLeaseCoordinator projectLeaseCoordinator,
        IOllamaFailureDiagnosticWriter failureDiagnosticWriter,
        IOptions<OllamaOptions> options,
        ILogger<StoryGenerationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _ollamaClient = ollamaClient;
        _promptBuilder = promptBuilder;
        _gpuCoordinator = gpuCoordinator;
        _projectLeaseCoordinator = projectLeaseCoordinator;
        _failureDiagnosticWriter = failureDiagnosticWriter;
        _options = options.Value;
        _logger = logger;
    }

    public StoryGenerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        IStoryPromptBuilder promptBuilder,
        IGpuGenerationCoordinator gpuCoordinator,
        IOllamaFailureDiagnosticWriter failureDiagnosticWriter,
        IOptions<OllamaOptions> options,
        ILogger<StoryGenerationService> logger)
        : this(dbContextFactory, ollamaClient, promptBuilder, gpuCoordinator, null!, failureDiagnosticWriter, options, logger)
    {
    }

    public async Task<StoryGenerationProgressResult> GenerateStoryNarrativeAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
        await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerating, cancellationToken);

        var existing = await LoadExistingStoryAsync(project.Id, cancellationToken);
        if (existing is not null)
        {
            Report(progress, "Story narrative", "Existing FilmStory checkpoint found; narrative generation skipped.", 0, project.CalculatedClipCount, 8, GenerationLogLevel.Success);
            return ToProgressResult(project.Id, existing, await CountScenesAsync(project.Id, cancellationToken));
        }

        await _ollamaClient.IsModelAvailableAsync(_options.StoryTextModel, cancellationToken);
        var systemPrompt = _promptBuilder.BuildStoryNarrativeSystemPrompt();
        var userPrompt = _promptBuilder.BuildStoryNarrativeUserPrompt(project);
        var settings = CreateStageGenerationSettings(project.Id, 0, "StoryNarrativeGeneration", SelectStageNumPredict(4096, 8192));
        var response = await GenerateWithOneRepairAsync<StoryNarrativeResponse>(
            [new OllamaChatMessage("system", systemPrompt), new OllamaChatMessage("user", userPrompt)],
            StoryJsonSchemas.StoryNarrativeSchema(),
            progress,
            "Story narrative",
            cancellationToken,
            _options.StoryTextModel,
            new OllamaFailureContext(project.Id, 0, "StoryNarrativeGeneration"),
            ValidateStoryNarrative,
            initialGenerationSettings: settings,
            freshRetryGenerationSettings: settings);

        var story = await SaveStoryNarrativeAsync(project.Id, response, cancellationToken);
        Report(progress, "Story narrative", "FilmStory narrative checkpoint saved.", 0, project.CalculatedClipCount, 10, GenerationLogLevel.Success);
        return ToProgressResult(project.Id, story, 0);
    }

    public async Task<StoryGenerationProgressResult> GenerateStoryCharactersAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
        var story = await LoadStorySnapshotByProjectAsync(project.Id, cancellationToken);
        if (story.Characters.Count > 0)
        {
            Report(progress, "Character continuity", "Existing StoryCharacter checkpoints found; character generation skipped.", 0, project.CalculatedClipCount, 15, GenerationLogLevel.Success);
            return ToProgressResult(project.Id, story, await CountScenesAsync(project.Id, cancellationToken));
        }

        await _ollamaClient.IsModelAvailableAsync(_options.StoryTextModel, cancellationToken);
        var settings = CreateStageGenerationSettings(project.Id, 0, "StoryCharacterGeneration", SelectStageNumPredict(2048, 4096));
        var response = await GenerateWithOneRepairAsync<StoryCharactersResponse>(
            [
                new OllamaChatMessage("system", _promptBuilder.BuildCharacterGenerationSystemPrompt()),
                new OllamaChatMessage("user", _promptBuilder.BuildCharacterGenerationUserPrompt(project, story))
            ],
            StoryJsonSchemas.StoryCharactersSchema(),
            progress,
            "Character continuity",
            cancellationToken,
            _options.StoryTextModel,
            new OllamaFailureContext(project.Id, 0, "StoryCharacterGeneration"),
            ValidateStoryCharactersContainer,
            initialGenerationSettings: settings,
            freshRetryGenerationSettings: settings);

        response = await RepairCharacterFieldsAsync(project, story, response, progress, cancellationToken);
        await SaveStoryCharactersAsync(story.Id, response.Characters, cancellationToken);
        var updatedStory = await LoadStorySnapshotAsync(story.Id, cancellationToken);
        Report(progress, "Character continuity", $"{response.Characters.Count} character checkpoint(s) saved.", 0, project.CalculatedClipCount, 18, GenerationLogLevel.Success);
        return ToProgressResult(project.Id, updatedStory, await CountScenesAsync(project.Id, cancellationToken));
    }

    public async Task<StoryGenerationProgressResult> GenerateAllMissingNarrativeScenesAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
        var story = await LoadStorySnapshotByProjectAsync(project.Id, cancellationToken);

        while (true)
        {
            var state = await LoadResumeStateAsync(project.Id, cancellationToken);
            EnsureNoDuplicateScenes(state, project.Id);
            var nextScene = FindFirstMissingScene(state.SceneNumbers, project.CalculatedClipCount);
            if (nextScene > project.CalculatedClipCount)
            {
                Report(progress, "Narrative scenes", "All narrative scene checkpoints are present.", project.CalculatedClipCount, project.CalculatedClipCount, 30, GenerationLogLevel.Success);
                return ToProgressResult(project.Id, story, project.CalculatedClipCount);
            }

            var scene = await GenerateNarrativeSceneAsync(project, story.Id, nextScene, progress, cancellationToken);
            await SaveNarrativeSceneAsync(project, story.Id, scene, cancellationToken);
            Report(progress, "Narrative scenes", $"Scene {nextScene} narrative checkpoint saved.", nextScene, project.CalculatedClipCount, MapProgress(18, 30, nextScene, project.CalculatedClipCount), GenerationLogLevel.Success, nextScene, nextScene);
        }
    }

    public async Task<StoryGenerationProgressResult> GenerateAllMissingImagePromptsAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
        var story = await LoadStorySnapshotByProjectAsync(project.Id, cancellationToken);
        await EnsureAllNarrativeScenesPresentAsync(project, cancellationToken);

        while (true)
        {
            var scene = await LoadFirstSceneMissingImagePromptAsync(project.Id, cancellationToken);
            if (scene is null)
            {
                Report(progress, "Image prompts", "All image prompts are present.", project.CalculatedClipCount, project.CalculatedClipCount, 35, GenerationLogLevel.Success);
                return ToProgressResult(project.Id, story, project.CalculatedClipCount);
            }

            var prompt = await GenerateImagePromptAsync(project, story.Id, scene.SceneNumber, progress, cancellationToken);
            await SaveImagePromptAsync(project.Id, prompt, cancellationToken);
            Report(progress, "Image prompts", $"Scene {scene.SceneNumber} image prompt saved.", scene.SceneNumber, project.CalculatedClipCount, MapProgress(30, 35, scene.SceneNumber, project.CalculatedClipCount), GenerationLogLevel.Success, scene.SceneNumber, scene.SceneNumber);
        }
    }

    public async Task<StoryGenerationProgressResult> GenerateAllMissingVideoPromptsAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
        var story = await LoadStorySnapshotByProjectAsync(project.Id, cancellationToken);
        await EnsureAllImagePromptsPresentAsync(project, cancellationToken);

        while (true)
        {
            var scene = await LoadFirstSceneMissingVideoPromptAsync(project.Id, cancellationToken);
            if (scene is null)
            {
                Report(progress, "Video prompts", "All video prompts are present.", project.CalculatedClipCount, project.CalculatedClipCount, 55, GenerationLogLevel.Success);
                return ToProgressResult(project.Id, story, project.CalculatedClipCount);
            }

            var prompt = await GenerateVideoPromptAsync(project, story.Id, scene.SceneNumber, progress, cancellationToken);
            await SaveVideoPromptAsync(project.Id, prompt, cancellationToken);
            Report(progress, "Video prompts", $"Scene {scene.SceneNumber} video prompt saved.", scene.SceneNumber, project.CalculatedClipCount, MapProgress(50, 55, scene.SceneNumber, project.CalculatedClipCount), GenerationLogLevel.Success, scene.SceneNumber, scene.SceneNumber);
        }
    }

    public async Task<StoryGenerationProgressResult> GenerateStoryAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);

        try
        {
            var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
            await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerating, cancellationToken);

            Report(progress, "Ollama kontrolu", "Ollama servisi kontrol ediliyor.", 0, project.CalculatedClipCount, 2, GenerationLogLevel.Information);
            var health = await _ollamaClient.CheckHealthAsync(cancellationToken);
            if (!health.IsAvailable)
            {
                throw new InvalidOperationException(health.Message);
            }

            Report(progress, "Ollama kontrolu", $"{_options.StoryTextModel} metin modeli kontrol ediliyor.", 0, project.CalculatedClipCount, 4, GenerationLogLevel.Information);
            await _ollamaClient.IsModelAvailableAsync(_options.StoryTextModel, cancellationToken);
            Report(progress, "Ollama kontrolu", $"{_options.StoryTextModel} metin modeli bulundu.", 0, project.CalculatedClipCount, 5, GenerationLogLevel.Success);

            var resumeState = await LoadResumeStateAsync(project.Id, cancellationToken);
            EnsureNoDuplicateScenes(resumeState, project.Id);
            if (resumeState.Story is not null)
            {
                Report(progress, "Devam", "Mevcut hikaye bulundu, tekrar uretilmeyecek.", resumeState.SceneNumbers.Count, project.CalculatedClipCount, 6, GenerationLogLevel.Success);
                Report(progress, "Devam", resumeState.CharacterCount == 0 ? "Karaktersiz gorsel hikaye bulundu." : $"{resumeState.CharacterCount} karakter bulundu.", resumeState.SceneNumbers.Count, project.CalculatedClipCount, 7, GenerationLogLevel.Success);
                Report(progress, "Devam", $"Kaydedilmis sahne sayisi: {resumeState.SceneNumbers.Count}/{project.CalculatedClipCount}", resumeState.SceneNumbers.Count, project.CalculatedClipCount, 8, GenerationLogLevel.Information);

                if (TryGetCompletionError(resumeState.SceneNumbers, resumeState.TotalDurationSeconds, project.CalculatedClipCount, project.ClipDurationSeconds) is null)
                {
                    await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerated, cancellationToken);
                    Report(progress, "Tamamlandi", "Hikaye ve 30 sahne zaten tamamlanmis.", project.CalculatedClipCount, project.CalculatedClipCount, 100, GenerationLogLevel.Success);
                    return new StoryGenerationProgressResult
                    {
                        FilmProjectId = project.Id,
                        FilmStoryId = resumeState.Story.Id,
                        Title = resumeState.Story.Title,
                        GeneratedSceneCount = project.CalculatedClipCount
                    };
                }

                var firstMissing = FindFirstMissingScene(resumeState.SceneNumbers, project.CalculatedClipCount);
                Report(progress, "Devam", $"Ilk eksik sahne: {firstMissing}", resumeState.SceneNumbers.Count, project.CalculatedClipCount, 9, GenerationLogLevel.Information);
                await GenerateAllMissingScenesCoreAsync(project, resumeState.Story.Id, progress, cancellationToken);
                var finalState = await LoadResumeStateAsync(project.Id, cancellationToken);
                var completionError = TryGetCompletionError(finalState.SceneNumbers, finalState.TotalDurationSeconds, project.CalculatedClipCount, project.ClipDurationSeconds);
                if (completionError is not null)
                {
                    throw new InvalidOperationException(completionError);
                }

                await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerated, cancellationToken);
                Report(progress, "Tamamlandi", "Hikaye ve 30 sahne tamamlandi.", project.CalculatedClipCount, project.CalculatedClipCount, 100, GenerationLogLevel.Success);
                return new StoryGenerationProgressResult
                {
                    FilmProjectId = project.Id,
                    FilmStoryId = finalState.Story!.Id,
                    Title = finalState.Story.Title,
                    GeneratedSceneCount = project.CalculatedClipCount
                };
            }

            Report(progress, "Film omurgasi", "Hikaye omurgasi hazirlaniyor.", 0, project.CalculatedClipCount, 8, GenerationLogLevel.Information);
            var bible = await GenerateStoryBibleWithCharacterRepairAsync(project, progress, cancellationToken);

            Report(progress, "Film omurgasi", "Karakter alanlari dogrulaniyor.", 0, project.CalculatedClipCount, 18, GenerationLogLevel.Information);
            ValidateStoryBible(bible);
            Report(progress, "Film omurgasi", "Karakterler kaydediliyor.", 0, project.CalculatedClipCount, 19, GenerationLogLevel.Information);
            var story = await SaveStoryBibleAsync(project.Id, bible, cancellationToken);
            Report(progress, "Film omurgasi", $"{bible.Characters.Count} karakter ve film omurgasi SQL'e kaydedildi.", 0, project.CalculatedClipCount, 20, GenerationLogLevel.Success);

            await GenerateAllMissingScenesCoreAsync(project, story.Id, progress, cancellationToken);

            await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerated, cancellationToken);
            Report(progress, "Tamamlandi", "Hikaye ve sahne plani kaydedildi.", project.CalculatedClipCount, project.CalculatedClipCount, 100, GenerationLogLevel.Success);

            return new StoryGenerationProgressResult
            {
                FilmProjectId = project.Id,
                FilmStoryId = story.Id,
                Title = story.Title,
                GeneratedSceneCount = project.CalculatedClipCount
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Hikaye uretimi iptal edildi. FilmProjectId: {FilmProjectId}", filmProjectId);
            await UpdateProjectStatusAsync(filmProjectId, FilmProjectStatus.StoryGenerating, CancellationToken.None);
            throw;
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Hikaye uretimi aktivite timeout ile durdu; checkpointler korunuyor. FilmProjectId: {FilmProjectId}", filmProjectId);
            await UpdateProjectStatusAsync(filmProjectId, FilmProjectStatus.StoryGenerating, CancellationToken.None);
            throw;
        }
        catch (StorySceneGenerationException ex)
        {
            _logger.LogWarning(ex, "Sahne cevabi dogrulanamadi; checkpointler korunuyor. FilmProjectId={FilmProjectId}; SceneNumber={SceneNumber}; Stage={Stage}; Diagnostic={DiagnosticPath}", filmProjectId, ex.SceneNumber, ex.Stage, ex.LogPath);
            await UpdateProjectStatusAsync(filmProjectId, FilmProjectStatus.StoryGenerating, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hikaye uretimi basarisiz oldu. FilmProjectId: {FilmProjectId}", filmProjectId);
            await MarkFailedIfPossibleAsync(filmProjectId, CancellationToken.None);
            throw;
        }
    }

    public Task<StoryGenerationProgressResult> GenerateAllMissingScenesAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        GenerateStoryAsync(filmProjectId, progress, cancellationToken);

    public async Task<StoryGenerationProgressResult> GenerateNextMissingSceneAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        return await GenerateNextMissingSceneCoreAsync(filmProjectId, progress, cancellationToken, checkModel: true);
    }

    public async Task<StoryGenerationProgressResult> GenerateUpToMissingScenesAsync(
        int filmProjectId,
        int maximumSceneCount,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (maximumSceneCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSceneCount));
        }

        await using var projectLease = await AcquireProjectLeaseAsync(filmProjectId, cancellationToken);
        StoryGenerationProgressResult? result = null;
        for (var index = 0; index < maximumSceneCount; index++)
        {
            result = await GenerateNextMissingSceneCoreAsync(filmProjectId, progress, cancellationToken, checkModel: index == 0);
        }

        return result ?? throw new InvalidOperationException("No scene generation was requested.");
    }

    private ValueTask<IAsyncDisposable> AcquireProjectLeaseAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        if (_projectLeaseCoordinator is null)
        {
            throw new InvalidOperationException("Project generation lease coordinator is not configured.");
        }

        return _projectLeaseCoordinator.AcquireAsync(filmProjectId, cancellationToken);
    }

    private async Task<StoryGenerationProgressResult> GenerateNextMissingSceneCoreAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken,
        bool checkModel)
    {
        var project = await LoadProjectSnapshotAsync(filmProjectId, cancellationToken);
        await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerating, cancellationToken);
        if (checkModel)
        {
            Report(progress, "Ollama kontrolu", $"{_options.StoryTextModel} metin modeli kontrol ediliyor.", 0, project.CalculatedClipCount, 2, GenerationLogLevel.Information);
            await _ollamaClient.IsModelAvailableAsync(_options.StoryTextModel, cancellationToken);
        }

        var state = await LoadResumeStateAsync(project.Id, cancellationToken);
        EnsureNoDuplicateScenes(state, project.Id);
        if (state.Story is null)
        {
            throw new InvalidOperationException("Mevcut FilmStory kaydi bulunmadan sahne resume baslatilamaz.");
        }

        Report(progress, "Devam", "Mevcut hikaye bulundu, tekrar uretilmeyecek.", state.SceneNumbers.Count, project.CalculatedClipCount, 5, GenerationLogLevel.Success);
        Report(progress, "Devam", state.CharacterCount == 0 ? "Karaktersiz gorsel hikaye bulundu." : $"{state.CharacterCount} karakter bulundu.", state.SceneNumbers.Count, project.CalculatedClipCount, 6, GenerationLogLevel.Success);
        Report(progress, "Devam", $"Kaydedilmis sahne sayisi: {state.SceneNumbers.Count}/{project.CalculatedClipCount}", state.SceneNumbers.Count, project.CalculatedClipCount, 7, GenerationLogLevel.Information);

        if (TryGetCompletionError(state.SceneNumbers, state.TotalDurationSeconds, project.CalculatedClipCount, project.ClipDurationSeconds) is null)
        {
            await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerated, cancellationToken);
            Report(progress, "Tamamlandi", "Hikaye ve 30 sahne zaten tamamlanmis.", project.CalculatedClipCount, project.CalculatedClipCount, 100, GenerationLogLevel.Success);
            return new StoryGenerationProgressResult { FilmProjectId = project.Id, FilmStoryId = state.Story.Id, Title = state.Story.Title, GeneratedSceneCount = project.CalculatedClipCount };
        }

        var firstMissing = FindFirstMissingScene(state.SceneNumbers, project.CalculatedClipCount);
        Report(progress, "Devam", $"Ilk eksik sahne: {firstMissing}", state.SceneNumbers.Count, project.CalculatedClipCount, 8, GenerationLogLevel.Information);
        var scene = await GenerateSingleScenePackageAsync(project, state.Story.Id, firstMissing, progress, cancellationToken);
        var saved = await SaveSceneBatchAsync(project, state.Story.Id, [scene], cancellationToken);
        if (!saved.Contains(firstMissing))
        {
            throw new InvalidOperationException($"{firstMissing}. sahne kaydedilemedi.");
        }

        var newState = await LoadResumeStateAsync(project.Id, cancellationToken);
        if (TryGetCompletionError(newState.SceneNumbers, newState.TotalDurationSeconds, project.CalculatedClipCount, project.ClipDurationSeconds) is null)
        {
            await UpdateProjectStatusAsync(project.Id, FilmProjectStatus.StoryGenerated, cancellationToken);
        }

        Report(progress, $"Sahne {firstMissing}", $"Sahne {firstMissing} kaydedildi. Toplam ilerleme: {newState.SceneNumbers.Count}/{project.CalculatedClipCount}", newState.SceneNumbers.Count, project.CalculatedClipCount, ProgressFor(newState.SceneNumbers.Count, project.CalculatedClipCount), GenerationLogLevel.Success, firstMissing, firstMissing);
        return new StoryGenerationProgressResult { FilmProjectId = project.Id, FilmStoryId = state.Story.Id, Title = state.Story.Title, GeneratedSceneCount = newState.SceneNumbers.Count };
    }

    private async Task GenerateAllMissingScenesCoreAsync(
        FilmProject project,
        int filmStoryId,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = await LoadResumeStateAsync(project.Id, cancellationToken);
            EnsureNoDuplicateScenes(state, project.Id);
            var nextScene = FindFirstMissingScene(state.SceneNumbers, project.CalculatedClipCount);
            if (nextScene > project.CalculatedClipCount)
            {
                return;
            }

            Report(progress, $"Sahne {nextScene}", $"Sahne {nextScene} hazirlaniyor.", state.SceneNumbers.Count, project.CalculatedClipCount, ProgressFor(state.SceneNumbers.Count, project.CalculatedClipCount), GenerationLogLevel.Information, nextScene, nextScene);
            var scene = await GenerateSingleScenePackageAsync(project, filmStoryId, nextScene, progress, cancellationToken);
            var saved = await SaveSceneBatchAsync(project, filmStoryId, [scene], cancellationToken);
            if (!saved.Contains(nextScene) && !await SceneExistsAsync(project.Id, nextScene, cancellationToken))
            {
                throw new InvalidOperationException($"{nextScene}. sahne checkpoint olarak kaydedilemedi.");
            }

            Report(progress, $"Sahne {nextScene}", $"Sahne {nextScene} kaydedildi. Ilerleme: {state.SceneNumbers.Count + 1}/{project.CalculatedClipCount}", state.SceneNumbers.Count + 1, project.CalculatedClipCount, ProgressFor(state.SceneNumbers.Count + 1, project.CalculatedClipCount), GenerationLogLevel.Success, nextScene, nextScene);
        }
    }

    private async Task<StoryCharactersResponse> RepairCharacterFieldsAsync(
        FilmProject project,
        FilmStory story,
        StoryCharactersResponse response,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var bible = ToStoryBible(story, response);
            var issues = StoryCharacterFieldValidator.ValidateIssues(bible);
            if (issues.Count == 0)
            {
                return response;
            }

            Report(progress, "Character continuity", "Character field correction patch requested.", 0, project.CalculatedClipCount, 17, GenerationLogLevel.Warning);
            var affectedCharacters = response.Characters
                .Where(character => issues.Any(issue => issue.CharacterKey.Equals(character.CharacterKey, StringComparison.OrdinalIgnoreCase) || issue.CharacterIndex == response.Characters.IndexOf(character)))
                .ToList();
            var settings = CreateStageGenerationSettings(project.Id, 0, "StoryCharacterFieldCorrection", SelectStageNumPredict(256, 512));
            var corrections = await GenerateWithOneRepairAsync<StoryCharacterCorrectionsResponse>(
                [
                    new OllamaChatMessage("system", _promptBuilder.BuildCharacterCorrectionSystemPrompt()),
                    new OllamaChatMessage("user", _promptBuilder.BuildCharacterCorrectionUserPrompt(affectedCharacters, issues))
                ],
                StoryJsonSchemas.StoryCharacterCorrectionsSchema(),
                progress,
                "Character field correction",
                cancellationToken,
                _options.StoryTextModel,
                new OllamaFailureContext(project.Id, 0, "StoryCharacterFieldCorrection"),
                ValidateCharacterCorrections,
                initialGenerationSettings: settings,
                freshRetryGenerationSettings: settings,
                repairGenerationSettings: settings);

            response = ApplyCharacterCorrections(response, corrections, issues);
        }

        StoryCharacterFieldValidator.Validate(ToStoryBible(story, response));
        return response;
    }

    internal static StoryCharactersResponse ApplyCharacterCorrections(
        StoryCharactersResponse response,
        StoryCharacterCorrectionsResponse corrections,
        IReadOnlyList<StoryCharacterValidationIssue> issues)
    {
        var allowedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "characterKey",
            "name",
            "role",
            "physicalDescription",
            "clothingDescription",
            "personalityDescription",
            "voiceDescription",
            "continuityDescription"
        };
        var issueFields = issues
            .Select(issue => (issue.CharacterKey, issue.FieldName, issue.CharacterIndex))
            .ToList();

        foreach (var correction in corrections.Corrections)
        {
            if (!allowedFields.Contains(correction.Field))
            {
                throw new InvalidOperationException($"Unsupported character correction field: {correction.Field}");
            }

            var indexIssue = issueFields.FirstOrDefault(issue =>
                issue.FieldName.Equals(correction.Field, StringComparison.OrdinalIgnoreCase) &&
                issue.CharacterKey.Equals(correction.CharacterKey, StringComparison.OrdinalIgnoreCase));
            var character = response.Characters.FirstOrDefault(item =>
                item.CharacterKey.Equals(correction.CharacterKey, StringComparison.OrdinalIgnoreCase));
            if (character is null && indexIssue.CharacterIndex >= 0 && indexIssue.CharacterIndex < response.Characters.Count)
            {
                character = response.Characters[indexIssue.CharacterIndex];
            }

            if (character is null)
            {
                throw new InvalidOperationException($"Character correction target not found: {correction.CharacterKey}");
            }

            ApplyCharacterField(character, correction.Field, correction.Value);
        }

        return response;
    }

    private static void ApplyCharacterField(StoryCharacterResponse character, string field, string value)
    {
        var trimmed = value.Trim();
        switch (field)
        {
            case "characterKey":
                character.CharacterKey = trimmed;
                break;
            case "name":
                character.Name = trimmed;
                break;
            case "role":
                character.Role = trimmed;
                break;
            case "physicalDescription":
                character.PhysicalDescription = trimmed;
                break;
            case "clothingDescription":
                character.ClothingDescription = trimmed;
                break;
            case "personalityDescription":
                character.PersonalityDescription = trimmed;
                break;
            case "voiceDescription":
                character.VoiceDescription = trimmed;
                break;
            case "continuityDescription":
                character.ContinuityDescription = trimmed;
                break;
            default:
                throw new InvalidOperationException($"Unsupported character correction field: {field}");
        }
    }

    private async Task<NarrativeSceneResponse> GenerateNarrativeSceneAsync(
        FilmProject project,
        int filmStoryId,
        int sceneNumber,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var story = await LoadStorySnapshotAsync(filmStoryId, cancellationToken);
        var previousContext = sceneNumber == 1
            ? OpeningSceneContinuityFromPreviousScene
            : await BuildPreviousSceneContextAsync(project.Id, sceneNumber, cancellationToken);
        var systemPrompt = _promptBuilder.BuildNarrativeSceneSystemPrompt();
        var userPrompt = _promptBuilder.BuildNarrativeSceneUserPrompt(project, story, sceneNumber, previousContext);
        var settings = CreateStageGenerationSettings(project.Id, sceneNumber, "NarrativeSceneGeneration", SelectStageNumPredict(1024, 2048));
        var response = await GenerateWithOneRepairAsync<NarrativeSceneResponse>(
            [new OllamaChatMessage("system", systemPrompt), new OllamaChatMessage("user", userPrompt)],
            StoryJsonSchemas.NarrativeSceneSchema(),
            progress,
            $"Narrative scene {sceneNumber}",
            cancellationToken,
            _options.SceneTextModel,
            new OllamaFailureContext(project.Id, sceneNumber, "NarrativeSceneGeneration"),
            candidate => ValidateNarrativeSceneResponse(candidate, sceneNumber, project.ClipDurationSeconds),
            initialGenerationSettings: settings,
            freshRetryGenerationSettings: settings);
        NormalizeNarrativeSceneContinuity(response, sceneNumber);
        return response;
    }

    private async Task<SceneImagePromptResponse> GenerateImagePromptAsync(
        FilmProject project,
        int filmStoryId,
        int sceneNumber,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var story = await LoadStorySnapshotAsync(filmStoryId, cancellationToken);
        var scene = await LoadSceneByNumberAsync(project.Id, sceneNumber, cancellationToken);
        var settings = CreateStageGenerationSettings(project.Id, sceneNumber, "SceneImagePromptGeneration", SelectStageNumPredict(512, 1024));
        var response = await GenerateWithOneRepairAsync<SceneImagePromptResponse>(
            [
                new OllamaChatMessage("system", _promptBuilder.BuildImagePromptSystemPrompt()),
                new OllamaChatMessage("user", _promptBuilder.BuildImagePromptUserPrompt(project, story, scene))
            ],
            StoryJsonSchemas.SceneImagePromptSchema(),
            progress,
            $"Image prompt {sceneNumber}",
            cancellationToken,
            _options.VisualPromptModel,
            new OllamaFailureContext(project.Id, sceneNumber, "SceneImagePromptGeneration"),
            candidate => ValidateImagePromptResponse(candidate, sceneNumber),
            initialGenerationSettings: settings,
            freshRetryGenerationSettings: settings,
            deterministicFallback: () => BuildDeterministicImagePromptFallback(project, scene));
        response.ImageNegativePrompt = SceneNegativePromptPolicy.SanitizeImage(response.ImageNegativePrompt);
        return response;
    }

    private async Task<SceneVideoPromptResponse> GenerateVideoPromptAsync(
        FilmProject project,
        int filmStoryId,
        int sceneNumber,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var story = await LoadStorySnapshotAsync(filmStoryId, cancellationToken);
        var scene = await LoadSceneByNumberAsync(project.Id, sceneNumber, cancellationToken);
        var settings = CreateStageGenerationSettings(project.Id, sceneNumber, "SceneVideoPromptGeneration", SelectStageNumPredict(512, 1024));
        var response = await GenerateWithOneRepairAsync<SceneVideoPromptResponse>(
            [
                new OllamaChatMessage("system", _promptBuilder.BuildVideoPromptSystemPrompt()),
                new OllamaChatMessage("user", _promptBuilder.BuildVideoPromptUserPrompt(project, story, scene, BuildVideoPromptContextSummary(scene)))
            ],
            StoryJsonSchemas.SceneVideoPromptSchema(),
            progress,
            $"Video prompt {sceneNumber}",
            cancellationToken,
            _options.VideoPromptModel,
            new OllamaFailureContext(project.Id, sceneNumber, "SceneVideoPromptGeneration"),
            candidate =>
            {
                ValidateVideoPromptResponse(candidate, sceneNumber);
                ValidateSilentVideoPromptFields(candidate.SceneNumber, candidate.VideoPrompt, candidate.VideoNegativePrompt);
            },
            initialGenerationSettings: settings,
            freshRetryGenerationSettings: settings,
            deterministicFallback: () => BuildDeterministicVideoPromptFallback(project, scene));
        response.VideoNegativePrompt = SceneNegativePromptPolicy.SanitizeVideo(response.VideoNegativePrompt);
        ValidateSilentVideoPromptFields(sceneNumber, response.VideoPrompt, response.VideoNegativePrompt);
        return response;
    }

    private async Task<FilmProject> LoadProjectSnapshotAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == filmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadi.");
    }

    private async Task<FilmStory?> LoadExistingStoryAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmStories
            .AsNoTracking()
            .Include(story => story.Characters)
            .FirstOrDefaultAsync(story => story.FilmProjectId == filmProjectId, cancellationToken);
    }

    private async Task<FilmStory> LoadStorySnapshotByProjectAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmStories
            .AsNoTracking()
            .Include(story => story.Characters)
            .FirstOrDefaultAsync(story => story.FilmProjectId == filmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("FilmStory checkpoint is required before this stage.");
    }

    private async Task<int> CountScenesAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes.AsNoTracking().CountAsync(scene => scene.FilmProjectId == filmProjectId, cancellationToken);
    }

    private async Task<FilmStory> SaveStoryNarrativeAsync(int filmProjectId, StoryNarrativeResponse narrative, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.FilmStories
            .Include(story => story.Characters)
            .FirstOrDefaultAsync(story => story.FilmProjectId == filmProjectId, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var story = new FilmStory
        {
            FilmProjectId = filmProjectId,
            Title = narrative.Title.Trim(),
            Logline = narrative.Logline.Trim(),
            Synopsis = narrative.Synopsis.Trim(),
            OpeningSummary = narrative.OpeningSummary.Trim(),
            DevelopmentSummary = narrative.DevelopmentSummary.Trim(),
            ClimaxSummary = narrative.ClimaxSummary.Trim(),
            EndingSummary = narrative.EndingSummary.Trim(),
            WorldDescription = narrative.WorldDescription.Trim(),
            VisualDirection = narrative.VisualDirection.Trim(),
            ContinuityRulesJson = JsonSerializer.Serialize(narrative.ContinuityRules, JsonOptions),
            CreatedAt = DateTime.Now
        };
        db.FilmStories.Add(story);
        await db.SaveChangesAsync(cancellationToken);
        return story;
    }

    private async Task SaveStoryCharactersAsync(int filmStoryId, IReadOnlyList<StoryCharacterResponse> characters, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var hasExistingCharacters = await db.StoryCharacters.AnyAsync(character => character.FilmStoryId == filmStoryId, cancellationToken);
        if (hasExistingCharacters)
        {
            return;
        }

        foreach (var character in characters.Select((value, index) => new { value, index }))
        {
            db.StoryCharacters.Add(new StoryCharacter
            {
                FilmStoryId = filmStoryId,
                CharacterKey = character.value.CharacterKey.Trim(),
                Name = character.value.Name.Trim(),
                Role = character.value.Role.Trim(),
                PhysicalDescription = character.value.PhysicalDescription.Trim(),
                ClothingDescription = character.value.ClothingDescription.Trim(),
                PersonalityDescription = character.value.PersonalityDescription.Trim(),
                VoiceDescription = character.value.VoiceDescription.Trim(),
                ContinuityDescription = character.value.ContinuityDescription.Trim(),
                ForbiddenChangesJson = JsonSerializer.Serialize(character.value.ForbiddenChanges, JsonOptions),
                SortOrder = character.index
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveNarrativeSceneAsync(FilmProject project, int filmStoryId, NarrativeSceneResponse scene, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.FilmScenes.AnyAsync(item => item.FilmProjectId == project.Id && item.SceneNumber == scene.SceneNumber, cancellationToken))
        {
            return;
        }

        db.FilmScenes.Add(new FilmScene
        {
            FilmProjectId = project.Id,
            FilmStoryId = filmStoryId,
            SceneNumber = scene.SceneNumber,
            DurationSeconds = project.ClipDurationSeconds,
            Title = scene.Title.Trim(),
            StoryBeat = scene.StoryBeat.Trim(),
            SceneDescription = scene.SceneDescription.Trim(),
            LocationDescription = scene.LocationDescription.Trim(),
            TimeOfDay = scene.TimeOfDay.Trim(),
            CharactersJson = JsonSerializer.Serialize(scene.Characters, JsonOptions),
            ContinuityFromPreviousScene = scene.ContinuityFromPreviousScene.Trim(),
            ImagePrompt = string.Empty,
            ImageNegativePrompt = string.Empty,
            VideoPrompt = string.Empty,
            VideoNegativePrompt = string.Empty,
            NarrationText = string.Empty,
            DialogueJson = "[]",
            ValidationChecklistJson = JsonSerializer.Serialize(new[] { scene.DialogueIntent.Trim() }, JsonOptions),
            Status = FilmSceneStatus.Planned,
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<FilmScene> LoadSceneByNumberAsync(int filmProjectId, int sceneNumber, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes
            .AsNoTracking()
            .FirstOrDefaultAsync(scene => scene.FilmProjectId == filmProjectId && scene.SceneNumber == sceneNumber, cancellationToken)
            ?? throw new InvalidOperationException($"Scene {sceneNumber} checkpoint not found.");
    }

    private async Task<FilmScene?> LoadFirstSceneMissingImagePromptAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == filmProjectId && string.IsNullOrWhiteSpace(scene.ImagePrompt))
            .OrderBy(scene => scene.SceneNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<FilmScene?> LoadFirstSceneMissingVideoPromptAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scenes = await BuildVideoPromptCheckpointQuery(db.FilmScenes.AsNoTracking(), filmProjectId)
            .ToListAsync(cancellationToken);

        return FindFirstMissingOrInvalidVideoPromptCheckpoint(scenes, filmProjectId)?.ToFilmScene();
    }

    internal static IQueryable<VideoPromptCheckpointScene> BuildVideoPromptCheckpointQuery(
        IQueryable<FilmScene> scenes,
        int filmProjectId) =>
        scenes
            .Where(scene => scene.FilmProjectId == filmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .Select(scene => new VideoPromptCheckpointScene
            {
                Id = scene.Id,
                FilmProjectId = scene.FilmProjectId,
                SceneNumber = scene.SceneNumber,
                VideoPrompt = scene.VideoPrompt,
                VideoNegativePrompt = scene.VideoNegativePrompt
            });

    internal static VideoPromptCheckpointScene? FindFirstMissingOrInvalidVideoPromptCheckpoint(
        IEnumerable<VideoPromptCheckpointScene> scenes,
        int filmProjectId) =>
        scenes
            .Where(scene => scene.FilmProjectId == filmProjectId)
            .OrderBy(scene => scene.SceneNumber)
            .FirstOrDefault(scene =>
                string.IsNullOrWhiteSpace(scene.VideoPrompt) ||
                string.IsNullOrWhiteSpace(scene.VideoNegativePrompt) ||
                HasInvalidSilentVideoPromptFields(scene.VideoPrompt, scene.VideoNegativePrompt));

    internal sealed class VideoPromptCheckpointScene
    {
        public int Id { get; set; }
        public int FilmProjectId { get; set; }
        public int SceneNumber { get; set; }
        public string VideoPrompt { get; set; } = string.Empty;
        public string VideoNegativePrompt { get; set; } = string.Empty;

        public FilmScene ToFilmScene() => new()
        {
            Id = Id,
            FilmProjectId = FilmProjectId,
            SceneNumber = SceneNumber,
            VideoPrompt = VideoPrompt,
            VideoNegativePrompt = VideoNegativePrompt
        };
    }

    private async Task SaveImagePromptAsync(int filmProjectId, SceneImagePromptResponse prompt, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.FilmScenes.FirstOrDefaultAsync(item => item.FilmProjectId == filmProjectId && item.SceneNumber == prompt.SceneNumber, cancellationToken)
            ?? throw new InvalidOperationException($"Scene {prompt.SceneNumber} checkpoint not found.");
        if (!string.IsNullOrWhiteSpace(scene.ImagePrompt))
        {
            return;
        }

        scene.ImagePrompt = prompt.ImagePrompt.Trim();
        scene.ImageNegativePrompt = prompt.ImageNegativePrompt.Trim();
        scene.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task SaveVideoPromptAsync(int filmProjectId, SceneVideoPromptResponse prompt, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.FilmScenes.FirstOrDefaultAsync(item => item.FilmProjectId == filmProjectId && item.SceneNumber == prompt.SceneNumber, cancellationToken)
            ?? throw new InvalidOperationException($"Scene {prompt.SceneNumber} checkpoint not found.");
        if (!string.IsNullOrWhiteSpace(scene.VideoPrompt) &&
            !string.IsNullOrWhiteSpace(scene.VideoNegativePrompt) &&
            !HasInvalidSilentVideoPromptFields(scene.VideoPrompt, scene.VideoNegativePrompt))
        {
            return;
        }

        scene.VideoPrompt = prompt.VideoPrompt.Trim();
        scene.VideoNegativePrompt = prompt.VideoNegativePrompt.Trim();
        scene.ValidationChecklistJson = JsonSerializer.Serialize(new[] { prompt.StartState.Trim(), prompt.MotionPlan.Trim(), prompt.EndState.Trim() }, JsonOptions);
        scene.Status = FilmSceneStatus.PromptReady;
        scene.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SceneMediaAsset?> LoadSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.SceneMediaAssets
            .AsNoTracking()
            .Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Image && item.IsSelected)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null || string.IsNullOrWhiteSpace(asset.FilePath) || !File.Exists(asset.FilePath) || new FileInfo(asset.FilePath).Length == 0)
        {
            return null;
        }

        return asset;
    }

    private static string BuildVideoPromptContextSummary(FilmScene scene) =>
        $"Image prompt: {LimitText(scene.ImagePrompt, 900)}\nImage negative prompt: {LimitText(scene.ImageNegativePrompt, 500)}\nClip duration seconds: {scene.DurationSeconds}";

    private SceneImagePromptResponse BuildDeterministicImagePromptFallback(FilmProject project, FilmScene scene)
    {
        _logger.LogWarning(
            "Deterministic image prompt fallback created. ProjectId={ProjectId}; SceneNumber={SceneNumber}; GenerationSource=DeterministicFallback",
            project.Id,
            scene.SceneNumber);
        var continuity = FirstNonEmpty(scene.ContinuityFromPreviousScene, "preserve established story continuity");
        var prompt = string.Join(", ", new[]
        {
            $"single cinematic still for scene {scene.SceneNumber}",
            LimitText(project.VisualStyle, 160),
            LimitText(scene.SceneDescription, 320),
            LimitText(scene.StoryBeat, 220),
            $"location: {LimitText(scene.LocationDescription, 180)}",
            $"time of day: {LimitText(scene.TimeOfDay, 80)}",
            $"continuity: {LimitText(continuity, 220)}",
            "clear character identities, consistent clothing, coherent composition, production-ready framing"
        }.Where(item => !string.IsNullOrWhiteSpace(item)));

        return new SceneImagePromptResponse
        {
            SceneNumber = scene.SceneNumber,
            ImagePrompt = LimitText(prompt, 850),
            ImageNegativePrompt = SceneNegativePromptPolicy.SanitizeImage("low quality, blurry, text, subtitles, watermark, logo, distorted anatomy, inconsistent clothing")
        };
    }

    private SceneVideoPromptResponse BuildDeterministicVideoPromptFallback(FilmProject project, FilmScene scene)
    {
        _logger.LogWarning(
            "Deterministic video prompt fallback created. ProjectId={ProjectId}; SceneNumber={SceneNumber}; GenerationSource=DeterministicFallback",
            project.Id,
            scene.SceneNumber);
        var visualAction = FirstNonEmpty(scene.SceneDescription, scene.StoryBeat, "the established subject continues the scene action");
        var continuity = FirstNonEmpty(scene.ContinuityFromPreviousScene, "preserve the previous visual continuity");
        var startState = $"Start with the established composition in {LimitText(scene.LocationDescription, 160)} during {LimitText(scene.TimeOfDay, 80)}.";
        var motionPlan = $"Animate only visible motion: {LimitText(visualAction, 260)}. Characters use readable body language and facial expression; environmental elements move subtly; camera motion follows the requested {LimitText(project.VideoStyle, 120)} style.";
        var endState = $"End on a stable composition that preserves identities, clothing, lighting, background layout and continuity: {LimitText(continuity, 220)}.";
        var videoPrompt = string.Join(" ", new[]
        {
            startState,
            motionPlan,
            endState,
            "No new objects, no scene transition, no sudden camera jump."
        });

        var response = new SceneVideoPromptResponse
        {
            SceneNumber = scene.SceneNumber,
            StartState = LimitText(startState, 280),
            MotionPlan = LimitText(motionPlan, 420),
            EndState = LimitText(endState, 300),
            VideoPrompt = LimitText(videoPrompt, 850),
            VideoNegativePrompt = SceneNegativePromptPolicy.SanitizeVideo("no sound, no music, no dialogue, no subtitles, scene transition, sudden camera jump, identity change, face morphing")
        };
        ValidateSilentVideoPromptFields(scene.SceneNumber, response.VideoPrompt, response.VideoNegativePrompt);
        return response;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static string LimitText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private async Task EnsureAllNarrativeScenesPresentAsync(FilmProject project, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var sceneNumbers = await db.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == project.Id)
            .Select(scene => scene.SceneNumber)
            .ToListAsync(cancellationToken);
        var expected = Enumerable.Range(1, project.CalculatedClipCount).ToHashSet();
        if (!expected.SetEquals(sceneNumbers))
        {
            throw new InvalidOperationException("Narrative scenes must be complete before prompt generation starts.");
        }
    }

    private async Task EnsureAllImagePromptsPresentAsync(FilmProject project, CancellationToken cancellationToken)
    {
        await EnsureAllNarrativeScenesPresentAsync(project, cancellationToken);
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var missing = await db.FilmScenes
            .AsNoTracking()
            .Where(scene => scene.FilmProjectId == project.Id &&
                (string.IsNullOrWhiteSpace(scene.ImagePrompt) || string.IsNullOrWhiteSpace(scene.ImageNegativePrompt)))
            .OrderBy(scene => scene.SceneNumber)
            .Select(scene => scene.SceneNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (missing > 0)
        {
            throw new InvalidOperationException($"All image prompts must be complete before video prompt generation starts. First missing scene={missing}.");
        }
    }

    private async Task<StoryResumeState> LoadResumeStateAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var story = await db.FilmStories
            .AsNoTracking()
            .Include(item => item.Characters)
            .FirstOrDefaultAsync(item => item.FilmProjectId == filmProjectId, cancellationToken);
        var scenes = await db.FilmScenes
            .AsNoTracking()
            .Where(item => item.FilmProjectId == filmProjectId)
            .Select(item => new { item.SceneNumber, item.DurationSeconds })
            .ToListAsync(cancellationToken);

        return new StoryResumeState(
            story,
            story?.Characters.Count ?? 0,
            scenes.Select(item => item.SceneNumber).OrderBy(item => item).ToHashSet(),
            scenes.Sum(item => item.DurationSeconds),
            scenes.GroupBy(item => item.SceneNumber).Count(group => group.Count() > 1));
    }

    private static void EnsureNoDuplicateScenes(StoryResumeState state, int filmProjectId)
    {
        if (state.DuplicateSceneGroups > 0)
        {
            throw new InvalidOperationException($"FilmProjectId={filmProjectId} için yinelenen SceneNumber kayıtları bulundu; üretim güvenli biçimde durduruldu.");
        }
    }

    internal async Task<T> GenerateWithOneRepairAsync<T>(
        IReadOnlyList<OllamaChatMessage> messages,
        object schema,
        IProgress<StoryGenerationProgress>? progress,
        string phase,
        CancellationToken cancellationToken,
        string? modelOverride = null,
        OllamaFailureContext? failureContext = null,
        Action<T>? validateResponse = null,
        int gpuProjectId = 0,
        int gpuSceneId = 0,
        OllamaGenerationSettings? initialGenerationSettings = null,
        OllamaGenerationSettings? freshRetryGenerationSettings = null,
        OllamaGenerationSettings? repairGenerationSettings = null,
        Func<T>? deterministicFallback = null)
    {
        var selectedModel = string.IsNullOrWhiteSpace(modelOverride) ? _options.StoryTextModel : modelOverride;
        var streamProgress = CreateOllamaStreamProgress(progress, phase);
        var initialSettings = initialGenerationSettings ?? CreateInitialGenerationSettings(failureContext);
        OllamaResponseException initialFailure;
        try
        {
            LogStructuredAttempt("initial", phase, selectedModel, messages, initialSettings);
            var result = await ExecuteGpuCallAsync(
                () => _ollamaClient.ChatStructuredDetailedAsync<T>(
                    messages,
                    schema,
                    selectedModel,
                    TimeSpan.FromMinutes(Math.Max(1, _options.SceneHardTimeoutMinutes)),
                    cancellationToken,
                    streamProgress,
                    initialSettings),
                failureContext?.FilmProjectId ?? gpuProjectId,
                failureContext?.SceneNumber ?? gpuSceneId,
                cancellationToken);
            ValidateDetailedResult(result, validateResponse);
            LogStructuredSuccess("initial", phase, selectedModel, result.Metadata, "passed");
            Report(progress, phase, "Ollama response alindi ve deserialize edildi.", 0, 0, null, GenerationLogLevel.Success);
            return result.Value;
        }
        catch (OllamaResponseException ex)
        {
            initialFailure = ex;
        }
        catch (TimeoutException ex)
        {
            initialFailure = CreateTimeoutResponseException(selectedModel, initialSettings, ex);
        }

        var initialLogPath = await WriteFailureDiagnosticAsync(failureContext, "initial", initialFailure, cancellationToken);
        if (RequiresFreshRetry(initialFailure))
        {
            _logger.LogWarning(
                "Structured response hit output-limit/repetition. Model={Model}; Stage={Stage}; ContentLength={ContentLength}; Done={Done}; DoneReason={DoneReason}; fresh retry will run once without raw echo. Diagnostic={DiagnosticPath}",
                selectedModel,
                initialFailure.Stage,
                initialFailure.ResponseContent.Length,
                initialFailure.Metadata.Done,
                initialFailure.Metadata.DoneReason,
                initialLogPath);
            Report(progress, phase, $"Model cevabi {initialFailure.Stage} olarak durdu; ham cikti atilip ayni 30B modelle tek fresh kisa yeniden uretim deneniyor.", 0, 0, null, GenerationLogLevel.Warning);
            return await RunFreshRetryAsync(
                messages,
                schema,
                progress,
                phase,
                cancellationToken,
                selectedModel,
                failureContext,
                validateResponse,
                streamProgress,
                gpuProjectId,
                gpuSceneId,
                freshRetryGenerationSettings,
                deterministicFallback);
        }

        _logger.LogWarning(
            "Structured response failed. Model={Model}; Stage={Stage}; ContentLength={ContentLength}; Done={Done}; DoneReason={DoneReason}; repair will run once. Diagnostic={DiagnosticPath}",
            selectedModel,
            initialFailure.Stage,
            initialFailure.ResponseContent.Length,
            initialFailure.Metadata.Done,
            initialFailure.Metadata.DoneReason,
            initialLogPath);
        Report(progress, phase, $"Model cevabi dogrulanamadi ({initialFailure.Stage}); ayni 30B modelle tek repair denemesi yapiliyor.", 0, 0, null, GenerationLogLevel.Warning);

        var repairMessages = new List<OllamaChatMessage>
        {
            new("system", "Repair one malformed structured response. Return exactly one JSON object matching the supplied schema. Return JSON only. Do not include markdown, code fences, explanations or commentary. Preserve the original sceneNumber and intended content. Keep values concise."),
            new("user", BuildRepairGuidance(failureContext)),
            new("user", $"Expected JSON schema:\n{JsonSerializer.Serialize(schema, JsonOptions)}\n\nMalformed response excerpt to repair; do not echo it verbatim:\n{BuildRepairExcerpt(initialFailure.ResponseContent)}")
        };
        var repairSettings = repairGenerationSettings ?? CreateRepairGenerationSettings(failureContext);

        try
        {
            LogStructuredAttempt("repair", phase, selectedModel, repairMessages, repairSettings);
            var repaired = await ExecuteGpuCallAsync(
                () => _ollamaClient.ChatStructuredDetailedAsync<T>(
                    repairMessages,
                    schema,
                    selectedModel,
                    TimeSpan.FromMinutes(Math.Max(1, _options.SceneHardTimeoutMinutes)),
                    cancellationToken,
                    streamProgress,
                    repairSettings),
                failureContext?.FilmProjectId ?? gpuProjectId,
                failureContext?.SceneNumber ?? gpuSceneId,
                cancellationToken);
            ValidateDetailedResult(repaired, validateResponse);
            LogStructuredSuccess("repair", phase, selectedModel, repaired.Metadata, "passed");
            Report(progress, phase, "Repair cevabi dogrulandi.", 0, 0, null, GenerationLogLevel.Success);
            return repaired.Value;
        }
        catch (OllamaResponseException repairFailure)
        {
            var repairLogPath = await WriteFailureDiagnosticAsync(failureContext, "repair", repairFailure, cancellationToken);
            _logger.LogWarning(
                "Repair response failed. Model={Model}; Stage={Stage}; ContentLength={ContentLength}; Done={Done}; DoneReason={DoneReason}; Diagnostic={DiagnosticPath}",
                selectedModel,
                repairFailure.Stage,
                repairFailure.ResponseContent.Length,
                repairFailure.Metadata.Done,
                repairFailure.Metadata.DoneReason,
                repairLogPath);
            return await RunFinalRecoveryAsync(
                messages,
                schema,
                progress,
                phase,
                cancellationToken,
                selectedModel,
                failureContext,
                validateResponse,
                streamProgress,
                gpuProjectId,
                gpuSceneId,
                deterministicFallback);
        }
        catch (TimeoutException repairTimeout)
        {
            var timeoutFailure = CreateTimeoutResponseException(selectedModel, repairSettings, repairTimeout);
            var repairLogPath = await WriteFailureDiagnosticAsync(failureContext, "repair", timeoutFailure, cancellationToken);
            _logger.LogWarning(
                repairTimeout,
                "Repair response timed out. Model={Model}; Stage={Stage}; Diagnostic={DiagnosticPath}",
                selectedModel,
                timeoutFailure.Stage,
                repairLogPath);
            return await RunFinalRecoveryAsync(
                messages,
                schema,
                progress,
                phase,
                cancellationToken,
                selectedModel,
                failureContext,
                validateResponse,
                streamProgress,
                gpuProjectId,
                gpuSceneId,
                deterministicFallback);
        }
    }

    private async Task<T> RunFreshRetryAsync<T>(
        IReadOnlyList<OllamaChatMessage> originalMessages,
        object schema,
        IProgress<StoryGenerationProgress>? progress,
        string phase,
        CancellationToken cancellationToken,
        string selectedModel,
        OllamaFailureContext? failureContext,
        Action<T>? validateResponse,
        IProgress<OllamaStreamProgress> streamProgress,
        int gpuProjectId,
        int gpuSceneId,
        OllamaGenerationSettings? freshRetryGenerationSettings,
        Func<T>? deterministicFallback)
    {
        var freshMessages = BuildFreshRetryMessages(originalMessages);
        var freshSettings = freshRetryGenerationSettings ?? CreateFreshRetryGenerationSettings(failureContext);
        try
        {
            LogStructuredAttempt("fresh", phase, selectedModel, freshMessages, freshSettings);
            var fresh = await ExecuteGpuCallAsync(
                () => _ollamaClient.ChatStructuredDetailedAsync<T>(
                    freshMessages,
                    schema,
                    selectedModel,
                    TimeSpan.FromMinutes(Math.Max(1, _options.SceneHardTimeoutMinutes)),
                    cancellationToken,
                    streamProgress,
                    freshSettings),
                failureContext?.FilmProjectId ?? gpuProjectId,
                failureContext?.SceneNumber ?? gpuSceneId,
                cancellationToken);
            ValidateDetailedResult(fresh, validateResponse);
            LogStructuredSuccess("fresh", phase, selectedModel, fresh.Metadata, "passed");
            Report(progress, phase, "Fresh kisa yeniden uretim cevabi dogrulandi.", 0, 0, null, GenerationLogLevel.Success);
            return fresh.Value;
        }
        catch (OllamaResponseException freshFailure)
        {
            var freshLogPath = await WriteFailureDiagnosticAsync(failureContext, "fresh", freshFailure, cancellationToken);
            _logger.LogWarning(
                "Fresh retry response failed. Model={Model}; Stage={Stage}; ContentLength={ContentLength}; Done={Done}; DoneReason={DoneReason}; Diagnostic={DiagnosticPath}",
                selectedModel,
                freshFailure.Stage,
                freshFailure.ResponseContent.Length,
                freshFailure.Metadata.Done,
                freshFailure.Metadata.DoneReason,
                freshLogPath);
            return await RunFinalRecoveryAsync(
                originalMessages,
                schema,
                progress,
                phase,
                cancellationToken,
                selectedModel,
                failureContext,
                validateResponse,
                streamProgress,
                gpuProjectId,
                gpuSceneId,
                deterministicFallback);
        }
        catch (TimeoutException freshTimeout)
        {
            var timeoutFailure = CreateTimeoutResponseException(selectedModel, freshSettings, freshTimeout);
            var freshLogPath = await WriteFailureDiagnosticAsync(failureContext, "fresh", timeoutFailure, cancellationToken);
            _logger.LogWarning(
                freshTimeout,
                "Fresh retry response timed out. Model={Model}; Stage={Stage}; Diagnostic={DiagnosticPath}",
                selectedModel,
                timeoutFailure.Stage,
                freshLogPath);
            return await RunFinalRecoveryAsync(
                originalMessages,
                schema,
                progress,
                phase,
                cancellationToken,
                selectedModel,
                failureContext,
                validateResponse,
                streamProgress,
                gpuProjectId,
                gpuSceneId,
                deterministicFallback);
        }
    }

    private async Task<T> RunFinalRecoveryAsync<T>(
        IReadOnlyList<OllamaChatMessage> originalMessages,
        object schema,
        IProgress<StoryGenerationProgress>? progress,
        string phase,
        CancellationToken cancellationToken,
        string selectedModel,
        OllamaFailureContext? failureContext,
        Action<T>? validateResponse,
        IProgress<OllamaStreamProgress> streamProgress,
        int gpuProjectId,
        int gpuSceneId,
        Func<T>? deterministicFallback)
    {
        var finalMessages = BuildFinalRecoveryMessages(originalMessages, schema, failureContext);
        var finalSettings = CreateFinalRecoveryGenerationSettings(failureContext);
        try
        {
            LogStructuredAttempt("final-regeneration", phase, selectedModel, finalMessages, finalSettings);
            var final = await ExecuteGpuCallAsync(
                () => _ollamaClient.ChatStructuredDetailedAsync<T>(
                    finalMessages,
                    schema,
                    selectedModel,
                    TimeSpan.FromMinutes(Math.Max(1, _options.SceneHardTimeoutMinutes)),
                    cancellationToken,
                    streamProgress,
                    finalSettings),
                failureContext?.FilmProjectId ?? gpuProjectId,
                failureContext?.SceneNumber ?? gpuSceneId,
                cancellationToken);
            ValidateDetailedResult(final, validateResponse);
            LogStructuredSuccess("final-regeneration", phase, selectedModel, final.Metadata, "passed");
            Report(progress, phase, "Final kısıtlı yeniden üretim cevabı doğrulandı.", 0, 0, null, GenerationLogLevel.Success);
            return final.Value;
        }
        catch (OllamaResponseException finalFailure)
        {
            var finalLogPath = await WriteFailureDiagnosticAsync(failureContext, "final-regeneration", finalFailure, cancellationToken);
            _logger.LogWarning(
                "Final regeneration failed. Model={Model}; Stage={Stage}; ContentLength={ContentLength}; Done={Done}; DoneReason={DoneReason}; Diagnostic={DiagnosticPath}",
                selectedModel,
                finalFailure.Stage,
                finalFailure.ResponseContent.Length,
                finalFailure.Metadata.Done,
                finalFailure.Metadata.DoneReason,
                finalLogPath);

            return UseFallbackOrThrow(
                deterministicFallback,
                validateResponse,
                progress,
                phase,
                failureContext,
                finalFailure,
                finalLogPath);
        }
        catch (TimeoutException finalTimeout)
        {
            var timeoutFailure = CreateTimeoutResponseException(selectedModel, finalSettings, finalTimeout);
            var finalLogPath = await WriteFailureDiagnosticAsync(failureContext, "final-regeneration", timeoutFailure, cancellationToken);
            _logger.LogWarning(
                finalTimeout,
                "Final regeneration timed out. Model={Model}; Stage={Stage}; Diagnostic={DiagnosticPath}",
                selectedModel,
                timeoutFailure.Stage,
                finalLogPath);

            return UseFallbackOrThrow(
                deterministicFallback,
                validateResponse,
                progress,
                phase,
                failureContext,
                timeoutFailure,
                finalLogPath);
        }
    }

    private T UseFallbackOrThrow<T>(
        Func<T>? deterministicFallback,
        Action<T>? validateResponse,
        IProgress<StoryGenerationProgress>? progress,
        string phase,
        OllamaFailureContext? failureContext,
        OllamaResponseException finalFailure,
        string finalLogPath)
    {
        if (deterministicFallback is null)
        {
            throw CreateSceneFailureOrOriginal(failureContext, finalFailure, finalLogPath);
        }

        var fallback = deterministicFallback();
        validateResponse?.Invoke(fallback);
        _logger.LogWarning(
            "Deterministic fallback used. ProjectId={ProjectId}; SceneNumber={SceneNumber}; Operation={Operation}; Phase={Phase}; GenerationSource=DeterministicFallback; PriorFailureStage={FailureStage}; Diagnostic={DiagnosticPath}",
            failureContext?.FilmProjectId,
            failureContext?.SceneNumber,
            failureContext?.OperationName,
            phase,
            finalFailure.Stage,
            finalLogPath);
        Report(progress, phase, "Model recovery bütçesi tükendi; deterministic fallback checkpoint üretildi.", 0, 0, null, GenerationLogLevel.Warning);
        return fallback;
    }

    private OllamaGenerationSettings? CreateInitialGenerationSettings(OllamaFailureContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var settings = CreateBaseGenerationSettings(context);
        if (IsSingleSceneGeneration(context))
        {
            ApplySceneStructuredSettings(settings, _options.SceneNumPredict);
        }

        return settings;
    }

    private OllamaGenerationSettings CreateFreshRetryGenerationSettings(OllamaFailureContext? context)
    {
        var settings = CreateBaseGenerationSettings(context);
        if (context is not null && IsSingleSceneGeneration(context))
        {
            ApplySceneStructuredSettings(settings, _options.SceneFreshRetryNumPredict);
        }
        else
        {
            settings.Temperature = 0.2;
            settings.TopP = 0.7;
            settings.NumPredict = _options.SceneFreshRetryNumPredict;
        }

        return settings;
    }

    private OllamaGenerationSettings CreateRepairGenerationSettings(OllamaFailureContext? context)
    {
        var settings = CreateBaseGenerationSettings(context);
        settings.Temperature = 0;
        settings.TopP = 0.1;
        settings.NumPredict = context is not null && IsSingleSceneGeneration(context)
            ? _options.SceneRepairNumPredict
            : Math.Max(_options.SceneNumPredict, _options.SceneRepairNumPredict);
        return settings;
    }

    private OllamaGenerationSettings CreateFinalRecoveryGenerationSettings(OllamaFailureContext? context)
    {
        var settings = CreateBaseGenerationSettings(context);
        settings.Temperature = 0;
        settings.TopP = 0.05;
        settings.TopK = 20;
        settings.RepeatPenalty = 1.05;
        settings.RepeatLastN = 1024;
        settings.NumPredict = context is not null && IsSingleSceneGeneration(context)
            ? _options.SceneRepairNumPredict
            : Math.Max(_options.SceneRepairNumPredict, 1024);
        return settings;
    }

    private static OllamaGenerationSettings CreateBaseGenerationSettings(OllamaFailureContext? context) =>
        new()
        {
            Think = false,
            OperationName = context?.OperationName,
            FilmProjectId = context?.FilmProjectId,
            SceneNumber = context?.SceneNumber
        };

    private void ApplySceneStructuredSettings(OllamaGenerationSettings settings, int numPredict)
    {
        settings.Temperature = _options.SceneStructuredTemperature;
        settings.TopP = _options.SceneStructuredTopP;
        settings.TopK = _options.SceneStructuredTopK;
        settings.RepeatPenalty = _options.SceneStructuredRepeatPenalty;
        settings.RepeatLastN = _options.SceneStructuredRepeatLastN;
        settings.NumPredict = numPredict;
    }

    private static IReadOnlyList<OllamaChatMessage> BuildFreshRetryMessages(IReadOnlyList<OllamaChatMessage> originalMessages)
    {
        var messages = originalMessages.ToList();
        messages.Add(new OllamaChatMessage(
            "user",
            "The previous attempt was discarded because it hit an output limit or repetition loop. Do not use, quote, continue or repair that output. Regenerate freshly from the original instructions only. Return one complete concise JSON object. Keep comma-separated negative prompts short, unique, and non-repeating."));
        return messages;
    }

    private static IReadOnlyList<OllamaChatMessage> BuildFinalRecoveryMessages(
        IReadOnlyList<OllamaChatMessage> originalMessages,
        object schema,
        OllamaFailureContext? failureContext)
    {
        var messages = originalMessages.ToList();
        messages.Add(new OllamaChatMessage(
            "user",
            "Final recovery attempt for the current atomic item only. Ignore every previous failed response. Return one minimal complete JSON object matching the schema exactly. Use the requested sceneNumber exactly. Fill every required field with concise production-safe text derived only from the source context. Do not include markdown, prose, comments, code fences, arrays unless the schema requires them, or extra keys."));
        if (failureContext is not null)
        {
            messages.Add(new OllamaChatMessage(
                "user",
                $"Authoritative checkpoint: ProjectId={failureContext.FilmProjectId}; SceneNumber={failureContext.SceneNumber}; Operation={failureContext.OperationName}. Do not rewrite any other scene."));
        }

        messages.Add(new OllamaChatMessage("user", $"Schema reminder:\n{JsonSerializer.Serialize(schema, JsonOptions)}"));
        return messages;
    }

    private static OllamaResponseException CreateTimeoutResponseException(
        string selectedModel,
        OllamaGenerationSettings? settings,
        TimeoutException exception) =>
        new OllamaIncompleteStreamException(
            exception.Message,
            string.Empty,
            new OllamaResponseMetadata
            {
                Model = selectedModel,
                Endpoint = "/api/chat",
                OperationName = settings?.OperationName ?? string.Empty,
                FilmProjectId = settings?.FilmProjectId,
                SceneNumber = settings?.SceneNumber
            },
            exception);

    private static string BuildRepairExcerpt(string responseContent)
    {
        if (responseContent.Length <= RepairExcerptMaxCharacters)
        {
            return responseContent;
        }

        var head = RepairExcerptMaxCharacters / 2;
        var tail = RepairExcerptMaxCharacters - head;
        return responseContent[..head] + "\n...[middle omitted from repair prompt]...\n" + responseContent[^tail..];
    }

    private static string BuildRepairGuidance(OllamaFailureContext? failureContext)
    {
        if (failureContext is null || !IsSingleSceneGeneration(failureContext))
        {
            return "Repair all validation errors while preserving the original intent.";
        }

        return failureContext.SceneNumber == 1
            ? $"Single-scene repair rule: for scene 1, continuityFromPreviousScene must be exactly \"{OpeningSceneContinuityFromPreviousScene}\" because there is no previous scene."
            : "Single-scene repair rule: for scene 2 and later, continuityFromPreviousScene is required and must briefly describe concrete visual, spatial, temporal or action continuity from the previous scene.";
    }

    private static bool RequiresFreshRetry(OllamaResponseException exception) =>
        exception is OllamaResponseTruncatedException or OllamaRepetitionDetectedException ||
        exception.Stage.Equals("TokenLimit", StringComparison.OrdinalIgnoreCase) ||
        exception.Stage.Equals("RepetitionDetected", StringComparison.OrdinalIgnoreCase);

    private static bool IsSingleSceneGeneration(OllamaFailureContext context) =>
        context.OperationName.Equals("SingleSceneGeneration", StringComparison.OrdinalIgnoreCase);

    private async Task<T> ExecuteGpuCallAsync<T>(
        Func<Task<T>> operation,
        int filmProjectId,
        int sceneId,
        CancellationToken cancellationToken)
    {
        if (_gpuCoordinator is null)
        {
            return await operation();
        }

        await using var gpuLease = await _gpuCoordinator.AcquireAsync(
            GenerationOperationType.OllamaText,
            filmProjectId,
            sceneId,
            cancellationToken);
        return await operation();
    }

    private void LogStructuredAttempt(
        string attemptType,
        string phase,
        string model,
        IReadOnlyList<OllamaChatMessage> messages,
        OllamaGenerationSettings? settings)
    {
        var promptCharacters = settings?.PromptCharacterCount ?? messages.Sum(message => message.Content?.Length ?? 0);
        var estimatedPromptTokens = settings?.EstimatedPromptTokens ?? EstimatePromptTokens(messages.Select(message => message.Content ?? string.Empty).ToArray());
        _logger.LogInformation(
            "Structured generation attempt. Phase={Phase}; Attempt={Attempt}; Model={Model}; Operation={Operation}; OutputProfile={OutputProfile}; PromptCharacters={PromptCharacters}; EstimatedPromptTokens={EstimatedPromptTokens}; Context={Context}; NumPredict={NumPredict}",
            phase,
            attemptType,
            model,
            settings?.OperationName ?? string.Empty,
            settings?.OutputProfile ?? "Default",
            promptCharacters,
            estimatedPromptTokens,
            _options.ContextLength,
            settings?.NumPredict ?? _options.SceneNumPredict);
    }

    private void LogStructuredSuccess(
        string attemptType,
        string phase,
        string model,
        OllamaResponseMetadata metadata,
        string validation)
    {
        _logger.LogInformation(
            "Structured generation completed. Phase={Phase}; Attempt={Attempt}; Model={Model}; Operation={Operation}; OutputProfile={OutputProfile}; PromptCharacters={PromptCharacters}; EstimatedPromptTokens={EstimatedPromptTokens}; PromptTokens={PromptTokens}; ResponseTokens={ResponseTokens}; ResponseCharacters={ResponseCharacters}; Done={Done}; DoneReason={DoneReason}; Validation={Validation}",
            phase,
            attemptType,
            model,
            metadata.OperationName,
            metadata.OutputProfile ?? "Default",
            metadata.PromptCharacterCount,
            metadata.EstimatedPromptTokens,
            metadata.PromptTokenCount,
            metadata.ResponseTokenCount,
            metadata.ResponseCharacterCount,
            metadata.Done,
            metadata.DoneReason,
            validation);
    }

    private static void ValidateDetailedResult<T>(OllamaStructuredResult<T> result, Action<T>? validator)
    {
        if (validator is null)
        {
            return;
        }

        try
        {
            validator(result.Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw new OllamaStructuredResponseException(
                "Model yaniti domain dogrulamasindan gecemedi.",
                "DomainValidation",
                result.RawResponse,
                result.Metadata,
                ex)
            {
                ValidationErrors = [ex.Message]
            };
        }
    }

    private async Task<string> WriteFailureDiagnosticAsync(
        OllamaFailureContext? context,
        string attemptType,
        OllamaResponseException exception,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return string.Empty;
        }

        try
        {
            return await _failureDiagnosticWriter.WriteAsync(context, attemptType, exception, cancellationToken);
        }
        catch (Exception diagnosticException)
        {
            _logger.LogError(diagnosticException, "Ollama failure diagnostic could not be written. ProjectId={ProjectId}; SceneNumber={SceneNumber}; Attempt={Attempt}", context.FilmProjectId, context.SceneNumber, attemptType);
            return string.Empty;
        }
    }

    private static Exception CreateSceneFailureOrOriginal(
        OllamaFailureContext? context,
        OllamaResponseException exception,
        string logPath) =>
        context is null || context.OperationName.StartsWith("StoryBible", StringComparison.OrdinalIgnoreCase)
            ? exception
            : new StorySceneGenerationException(context.FilmProjectId, context.SceneNumber, exception.Stage, logPath, exception);

    internal static StoryBibleOutputProfile SelectStoryBibleOutputProfile(FilmProject project) =>
        project.CalculatedClipCount <= 2 && !project.UseNarrator
            ? StoryBibleOutputProfile.BriefVisual
            : StoryBibleOutputProfile.Detailed;

    internal StoryBibleOutputBudget CalculateStoryBibleOutputBudget(
        FilmProject project,
        StoryBibleOutputProfile profile,
        StoryBibleGenerationAttempt attempt,
        int estimatedPromptTokens)
    {
        var desired = attempt switch
        {
            StoryBibleGenerationAttempt.Initial when profile == StoryBibleOutputProfile.BriefVisual => StoryBibleBriefInitialNumPredict,
            StoryBibleGenerationAttempt.FreshRetry when profile == StoryBibleOutputProfile.BriefVisual => StoryBibleBriefRetryNumPredict,
            StoryBibleGenerationAttempt.CharacterRepair when profile == StoryBibleOutputProfile.BriefVisual => StoryBibleBriefInitialNumPredict,
            StoryBibleGenerationAttempt.CharacterRepair => Math.Max(_options.SceneRepairNumPredict, _options.SceneNumPredict),
            StoryBibleGenerationAttempt.FreshRetry => Math.Clamp(_options.SceneFreshRetryNumPredict, _options.SceneNumPredict, StoryBibleDetailedMaxNumPredict),
            _ => Math.Clamp(2048 + project.CalculatedClipCount * 160, _options.SceneNumPredict, StoryBibleDetailedMaxNumPredict)
        };

        var contextCap = Math.Max(StoryBibleMinimumNumPredict, _options.ContextLength - estimatedPromptTokens - StoryBibleContextMarginTokens);
        var cappedMaximum = Math.Min(StoryBibleDetailedMaxNumPredict, contextCap);
        var numPredict = Math.Min(Math.Max(desired, StoryBibleMinimumNumPredict), cappedMaximum);
        return new StoryBibleOutputBudget(
            profile,
            attempt,
            numPredict,
            estimatedPromptTokens,
            _options.ContextLength,
            StoryBibleContextMarginTokens,
            desired,
            cappedMaximum);
    }

    private OllamaGenerationSettings CreateStoryBibleGenerationSettings(
        FilmProject project,
        IReadOnlyList<OllamaChatMessage> messages,
        StoryBibleOutputProfile profile,
        StoryBibleGenerationAttempt attempt)
    {
        var promptCharacters = messages.Sum(message => message.Content?.Length ?? 0);
        var estimatedPromptTokens = EstimatePromptTokens(messages.Select(message => message.Content ?? string.Empty).ToArray());
        var budget = CalculateStoryBibleOutputBudget(project, profile, attempt, estimatedPromptTokens);
        var operationName = attempt == StoryBibleGenerationAttempt.CharacterRepair
            ? "StoryBibleCharacterRepair"
            : "StoryBibleGeneration";
        var settings = CreateBaseGenerationSettings(new OllamaFailureContext(project.Id, 0, operationName));
        settings.Temperature = profile == StoryBibleOutputProfile.BriefVisual ? 0.2 : _options.SceneStructuredTemperature;
        settings.TopP = profile == StoryBibleOutputProfile.BriefVisual ? 0.65 : _options.SceneStructuredTopP;
        settings.TopK = _options.SceneStructuredTopK;
        settings.RepeatPenalty = _options.SceneStructuredRepeatPenalty;
        settings.RepeatLastN = _options.SceneStructuredRepeatLastN;
        settings.NumPredict = budget.NumPredict;
        settings.OutputProfile = profile.ToString();
        settings.PromptCharacterCount = promptCharacters;
        settings.EstimatedPromptTokens = estimatedPromptTokens;
        return settings;
    }

    internal async Task<StoryBibleResponse> GenerateStoryBibleWithCharacterRepairAsync(
        FilmProject project,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var profile = SelectStoryBibleOutputProfile(project);
        var messages = new List<OllamaChatMessage>
        {
            new("system", _promptBuilder.BuildStoryBibleSystemPrompt()),
            new("user", profile == StoryBibleOutputProfile.BriefVisual
                ? _promptBuilder.BuildStoryBibleConciseUserPrompt(project)
                : _promptBuilder.BuildStoryBibleUserPrompt(project))
        };
        var initialSettings = CreateStoryBibleGenerationSettings(project, messages, profile, StoryBibleGenerationAttempt.Initial);
        var freshSettings = CreateStoryBibleGenerationSettings(project, BuildFreshRetryMessages(messages), profile, StoryBibleGenerationAttempt.FreshRetry);
        var storyBibleContext = new OllamaFailureContext(project.Id, 0, "StoryBibleGeneration");

        var bible = await GenerateWithOneRepairAsync<StoryBibleResponse>(
            messages,
            StoryJsonSchemas.StoryBibleSchema(),
            progress,
            "Film omurgasi",
            cancellationToken,
            failureContext: storyBibleContext,
            gpuProjectId: project.Id,
            initialGenerationSettings: initialSettings,
            freshRetryGenerationSettings: freshSettings);
        Report(progress, "Film omurgasi", "Hikaye omurgasi alindi.", 0, project.CalculatedClipCount, 16, GenerationLogLevel.Success);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var issues = StoryCharacterFieldValidator.ValidateIssues(bible);
            if (issues.Count == 0)
            {
                return bible;
            }

            Report(progress, "Film omurgasi", "Karakter alanlari duzeltiliyor.", 0, project.CalculatedClipCount, 17, GenerationLogLevel.Warning);
            _logger.LogWarning("Story bible character validation failed. FilmProjectId={FilmProjectId}; Issues={Issues}",
                project.Id,
                string.Join(" | ", issues.Select(issue => $"Index={issue.CharacterIndex}; Field={issue.FieldName}; ActualLength={issue.ActualLength}; Max={issue.MaxLength}; Reason={issue.Reason}")));

            var repairMessages = messages
                .Concat(new[]
                {
                    new OllamaChatMessage("assistant", JsonSerializer.Serialize(bible, JsonOptions)),
                    new OllamaChatMessage("user", BuildCharacterRepairPrompt(issues))
                })
                .ToList();
            var characterRepairSettings = CreateStoryBibleGenerationSettings(project, repairMessages, profile, StoryBibleGenerationAttempt.CharacterRepair);
            var characterRepairFreshSettings = CreateStoryBibleGenerationSettings(project, BuildFreshRetryMessages(repairMessages), profile, StoryBibleGenerationAttempt.FreshRetry);
            bible = await GenerateWithOneRepairAsync<StoryBibleResponse>(
                repairMessages,
                StoryJsonSchemas.StoryBibleSchema(),
                progress,
                "Film omurgasi repair",
                cancellationToken,
                _options.StoryTextModel,
                new OllamaFailureContext(project.Id, 0, "StoryBibleCharacterRepair"),
                candidate => StoryCharacterFieldValidator.Validate(candidate),
                gpuProjectId: project.Id,
                initialGenerationSettings: characterRepairSettings,
                freshRetryGenerationSettings: characterRepairFreshSettings);
        }

        StoryCharacterFieldValidator.Validate(bible);
        return bible;
    }

    private static string BuildCharacterRepairPrompt(IReadOnlyList<StoryCharacterValidationIssue> issues)
    {
        var issueText = string.Join(Environment.NewLine, issues.Select(issue =>
            $"- character index {issue.CharacterIndex}, key '{issue.CharacterKey}', field {issue.FieldName}, length {issue.ActualLength}/{issue.MaxLength}: {issue.Reason}"));

        return $"""
Fix only the characters array field placement problems listed below. Return the full StoryBible JSON again with the same title, plot summaries, world, visual direction and continuity rules.
Do not invent new characters. Do not change characterKey values unless empty or duplicated.
Role must be only a short narrative function, maximum 80 characters, for example: Protagonist, Ruler, Warrior Ally, Commander, Political Antagonist.
Move appearance details to physicalDescription. Move clothing, fur, leather, armor, weapons and equipment details to clothingDescription.
Do not truncate a clothing or physical description into role.
Validation issues:
{issueText}
""";
    }

    private async Task<FilmStory> SaveStoryBibleAsync(int filmProjectId, StoryBibleResponse bible, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var existingStory = await db.FilmStories
            .AsSplitQuery()
            .Include(story => story.Characters)
            .Include(story => story.Scenes)
            .FirstOrDefaultAsync(story => story.FilmProjectId == filmProjectId, cancellationToken);

        if (existingStory is not null)
        {
            db.FilmStories.Remove(existingStory);
            await db.SaveChangesAsync(cancellationToken);
        }

        var story = new FilmStory
        {
            FilmProjectId = filmProjectId,
            Title = bible.Title.Trim(),
            Logline = bible.Logline.Trim(),
            Synopsis = bible.Synopsis.Trim(),
            OpeningSummary = bible.OpeningSummary.Trim(),
            DevelopmentSummary = bible.DevelopmentSummary.Trim(),
            ClimaxSummary = bible.ClimaxSummary.Trim(),
            EndingSummary = bible.EndingSummary.Trim(),
            WorldDescription = bible.WorldDescription.Trim(),
            VisualDirection = bible.VisualDirection.Trim(),
            ContinuityRulesJson = JsonSerializer.Serialize(bible.ContinuityRules, JsonOptions),
            CreatedAt = DateTime.Now
        };

        foreach (var character in bible.Characters.Select((value, index) => new { value, index }))
        {
            story.Characters.Add(new StoryCharacter
            {
                CharacterKey = character.value.CharacterKey.Trim(),
                Name = character.value.Name.Trim(),
                Role = character.value.Role.Trim(),
                PhysicalDescription = character.value.PhysicalDescription.Trim(),
                ClothingDescription = character.value.ClothingDescription.Trim(),
                PersonalityDescription = character.value.PersonalityDescription.Trim(),
                VoiceDescription = character.value.VoiceDescription.Trim(),
                ContinuityDescription = character.value.ContinuityDescription.Trim(),
                ForbiddenChangesJson = JsonSerializer.Serialize(character.value.ForbiddenChanges, JsonOptions),
                SortOrder = character.index
            });
        }

        db.FilmStories.Add(story);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return story;
    }

    private async Task<List<SceneOutlineItemDto>> GenerateOutlinesAsync(
        FilmProject project,
        FilmStory story,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var outlines = new List<SceneOutlineItemDto>(project.CalculatedClipCount);
        for (var start = 1; start <= project.CalculatedClipCount; start += OutlineBlockSize)
        {
            var end = Math.Min(project.CalculatedClipCount, start + OutlineBlockSize - 1);
            var previousContext = BuildPreviousOutlineContext(outlines);
            Report(progress, "Sahne plani", $"{start}-{end}. sahnelerin kisa plani hazirlaniyor.", outlines.Count, project.CalculatedClipCount, 25, GenerationLogLevel.Information, start, end);

            var response = await GenerateWithOneRepairAsync<SceneOutlineBatchResponse>(
                new[]
                {
                    new OllamaChatMessage("system", _promptBuilder.BuildSceneOutlineSystemPrompt()),
                    new OllamaChatMessage("user", _promptBuilder.BuildSceneOutlineUserPrompt(project, story, start, end, previousContext))
                },
                StoryJsonSchemas.SceneOutlineBatchSchema(),
                progress,
                "Sahne plani",
                cancellationToken,
                gpuProjectId: project.Id,
                gpuSceneId: start);

            ValidateSceneNumbers(response.Scenes.Select(scene => scene.SceneNumber), start, end, "Sahne plani blogu");
            outlines.AddRange(response.Scenes.OrderBy(scene => scene.SceneNumber).Select(scene => new SceneOutlineItemDto
            {
                SceneNumber = scene.SceneNumber,
                Title = scene.Title,
                StoryBeat = scene.StoryBeat,
                ShortDescription = scene.ShortDescription,
                Characters = scene.Characters,
                Location = scene.Location,
                TimeOfDay = scene.TimeOfDay,
                ContinuityFromPreviousScene = scene.ContinuityFromPreviousScene
            }));
            Report(progress, "Sahne plani", $"{start}-{end}. sahne plani dogrulandi.", outlines.Count, project.CalculatedClipCount, 35, GenerationLogLevel.Success, start, end);
        }

        return outlines;
    }

    private async Task GenerateAndSaveScenePackagesAsync(
        FilmProject project,
        int filmStoryId,
        IReadOnlyList<SceneOutlineItemDto> outlines,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, _options.SceneBatchSize);
        var completed = 0;
        var totalBatches = (int)Math.Ceiling(outlines.Count / (double)batchSize);
        var batchNumber = 0;

        for (var index = 0; index < outlines.Count; index += batchSize)
        {
            batchNumber++;
            var batch = outlines.Skip(index).Take(batchSize).ToList();
            var start = batch.First().SceneNumber;
            var end = batch.Last().SceneNumber;
            var previousContext = BuildPreviousOutlineContext(outlines.Take(index).ToList());
            var percentage = 35 + (batchNumber - 1) * 60d / Math.Max(1, totalBatches);

            Report(progress, $"Batch {batchNumber}/{totalBatches}", $"{start}-{end}. sahneler ayrintilandiriliyor.", completed, project.CalculatedClipCount, percentage, GenerationLogLevel.Information, start, end);
            _logger.LogInformation("Scene package batch olusturuluyor. FilmProjectId: {FilmProjectId}, Start: {Start}, End: {End}, Batch: {Batch}", project.Id, start, end, batchNumber);

            var storySnapshot = await LoadStorySnapshotAsync(filmStoryId, cancellationToken);
            var messages = new List<OllamaChatMessage>
            {
                new("system", _promptBuilder.BuildScenePackageSystemPrompt()),
                new("user", _promptBuilder.BuildScenePackageUserPrompt(project, storySnapshot, batch, previousContext))
            };

            var response = await GenerateWithOneRepairAsync<ScenePackageBatchResponse>(
                messages,
                StoryJsonSchemas.ScenePackageBatchSchema(),
                progress,
                $"Batch {batchNumber}/{totalBatches}",
                cancellationToken,
                gpuProjectId: project.Id,
                gpuSceneId: start);

            ValidateSceneNumbers(response.Scenes.Select(scene => scene.SceneNumber), start, end, "Sahne paketi blogu");
            try
            {
                Report(progress, $"Batch {batchNumber}/{totalBatches}", "Sessiz video prompt kontrolu yapiliyor.", completed, project.CalculatedClipCount, percentage + 3, GenerationLogLevel.Information, start, end);
                ValidateSilentVideoPrompts(response.Scenes);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Scene package batch sessiz video kuralini ihlal etti. Tek repair denemesi yapilacak. FilmProjectId: {FilmProjectId}, Start: {Start}, End: {End}", project.Id, start, end);
                Report(progress, $"Batch {batchNumber}/{totalBatches}", "Sessiz video kural ihlali bulundu; tek repair denemesi yapiliyor.", completed, project.CalculatedClipCount, percentage + 4, GenerationLogLevel.Warning, start, end);
                var repairMessages = messages
                    .Append(new OllamaChatMessage("user", "The previous scene package violated the silent-video rule. Regenerate the same scene package JSON. Keep all sceneNumber values exactly the same. Rewrite every videoPrompt and videoNegativePrompt so they contain no narration, dialogue, spoken words, voice, sound, audio, music, ambient audio, sound effects, lip-sync or subtitles. Video prompts must describe only visible motion, camera movement, expressions, body movement, lighting changes, continuity preservation and final position. Return strict JSON only."))
                    .ToList();

                response = await GenerateWithOneRepairAsync<ScenePackageBatchResponse>(
                    repairMessages,
                    StoryJsonSchemas.ScenePackageBatchSchema(),
                    progress,
                    $"Batch {batchNumber}/{totalBatches}",
                    cancellationToken,
                    gpuProjectId: project.Id,
                    gpuSceneId: start);

                ValidateSceneNumbers(response.Scenes.Select(scene => scene.SceneNumber), start, end, "Sahne paketi repair blogu");
                SanitizeSilentVideoPrompts(response.Scenes);
                ValidateSilentVideoPrompts(response.Scenes);
            }

            await SaveSceneBatchAsync(project, filmStoryId, response.Scenes.OrderBy(scene => scene.SceneNumber).ToList(), cancellationToken);
            completed += batch.Count;
            Report(progress, "Veritabani", $"{start}-{end}. sahneler SQL'e kaydedildi.", completed, project.CalculatedClipCount, 35 + batchNumber * 60d / Math.Max(1, totalBatches), GenerationLogLevel.Success, start, end);
        }
    }

    private async Task GenerateMissingScenePackagesAsync(
        FilmProject project,
        int filmStoryId,
        HashSet<int> existingSceneNumbers,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        while (existingSceneNumbers.Count < project.CalculatedClipCount)
        {
            var firstMissing = FindFirstMissingScene(existingSceneNumbers, project.CalculatedClipCount);
            var start = GetBatchStart(firstMissing);
            var end = Math.Min(project.CalculatedClipCount, start + ResumeSceneBatchSize - 1);
            var startedAt = DateTime.Now;
            Report(progress, $"Sahne paketi {start}-{end}", $"Sahne paketi {start}-{end} hazirlaniyor. Model={_options.Model}; Baslangic={startedAt:HH:mm:ss}", existingSceneNumbers.Count, project.CalculatedClipCount, ProgressFor(existingSceneNumbers.Count, project.CalculatedClipCount), GenerationLogLevel.Information, start, end);

            var storySnapshot = await LoadStorySnapshotAsync(filmStoryId, cancellationToken);
            var outlines = await GenerateOutlinesForRangeWithTimeoutRetryAsync(project, storySnapshot, start, end, progress, cancellationToken);
            var previousContext = await BuildPreviousSceneContextAsync(project.Id, start, cancellationToken);
            var messages = new List<OllamaChatMessage>
            {
                new("system", _promptBuilder.BuildScenePackageSystemPrompt()),
                new("user", _promptBuilder.BuildScenePackageUserPrompt(project, storySnapshot, outlines, previousContext))
            };

            Report(progress, $"Sahne paketi {start}-{end}", "Qwen cevabi bekleniyor.", existingSceneNumbers.Count, project.CalculatedClipCount, null, GenerationLogLevel.Information, start, end);
            var response = await GenerateScenePackageWithTimeoutRetryAsync(project, messages, start, end, progress, cancellationToken);
            Report(progress, $"Sahne paketi {start}-{end}", $"Sahne paketi {start}-{end} dogrulaniyor.", existingSceneNumbers.Count, project.CalculatedClipCount, null, GenerationLogLevel.Information, start, end);
            var characters = await LoadStoryCharactersAsync(filmStoryId, cancellationToken);
            ValidateScenePackageBatch(response.Scenes, start, end, characters, project.ClipDurationSeconds);
            SanitizeSilentVideoPrompts(response.Scenes);
            ValidateSilentVideoPrompts(response.Scenes);

            var saved = await SaveSceneBatchAsync(project, filmStoryId, response.Scenes.OrderBy(scene => scene.SceneNumber).ToList(), cancellationToken);
            foreach (var sceneNumber in Enumerable.Range(start, end - start + 1))
            {
                if (saved.Contains(sceneNumber) || await SceneExistsAsync(project.Id, sceneNumber, cancellationToken))
                {
                    existingSceneNumbers.Add(sceneNumber);
                }
            }

            var elapsed = DateTime.Now - startedAt;
            Report(progress, $"Sahne paketi {start}-{end}", $"Sahne paketi {start}-{end} kaydedildi. Kaydedilen yeni sahne={saved.Count}; Gecen sure={elapsed:mm\\:ss}; Toplam ilerleme: {existingSceneNumbers.Count}/{project.CalculatedClipCount}", existingSceneNumbers.Count, project.CalculatedClipCount, ProgressFor(existingSceneNumbers.Count, project.CalculatedClipCount), GenerationLogLevel.Success, start, end);
        }
    }

    private async Task<List<SceneOutlineItemDto>> GenerateOutlinesForRangeAsync(
        FilmProject project,
        FilmStory story,
        int start,
        int end,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var previousContext = await BuildPreviousSceneContextAsync(project.Id, start, cancellationToken);
        var response = await GenerateWithOneRepairAsync<SceneOutlineBatchResponse>(
            new[]
            {
                new OllamaChatMessage("system", _promptBuilder.BuildSceneOutlineSystemPrompt()),
                new OllamaChatMessage("user", _promptBuilder.BuildSceneOutlineUserPrompt(project, story, start, end, previousContext))
            },
            StoryJsonSchemas.SceneOutlineBatchSchema(),
            progress,
            $"Sahne plani {start}-{end}",
            cancellationToken,
            gpuProjectId: project.Id,
            gpuSceneId: start);

        ValidateSceneNumbers(response.Scenes.Select(scene => scene.SceneNumber), start, end, "Sahne plani resume blogu");
        return response.Scenes.OrderBy(scene => scene.SceneNumber).Select(scene => new SceneOutlineItemDto
        {
            SceneNumber = scene.SceneNumber,
            Title = scene.Title,
            StoryBeat = scene.StoryBeat,
            ShortDescription = scene.ShortDescription,
            Characters = scene.Characters,
            Location = scene.Location,
            TimeOfDay = scene.TimeOfDay,
            ContinuityFromPreviousScene = scene.ContinuityFromPreviousScene
        }).ToList();
    }

    private async Task<ScenePackageItemResponse> GenerateSingleScenePackageAsync(
        FilmProject project,
        int filmStoryId,
        int sceneNumber,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var story = await LoadStorySnapshotAsync(filmStoryId, cancellationToken);
        var previousContext = await BuildPreviousSceneContextAsync(project.Id, sceneNumber, cancellationToken);
        var systemPrompt = _promptBuilder.BuildSingleScenePackageSystemPrompt();
        var userPrompt = _promptBuilder.BuildSingleScenePackageUserPrompt(project, story, sceneNumber, previousContext);
        var characters = await LoadStoryCharactersAsync(filmStoryId, cancellationToken);
        var promptLength = systemPrompt.Length + userPrompt.Length;
        var estimatedPromptTokens = EstimatePromptTokens(systemPrompt, userPrompt);
        Report(progress, $"Sahne {sceneNumber}", $"Sahne {sceneNumber} hazirlaniyor. Model={_options.SceneTextModel}; Prompt={promptLength} karakter / ~{estimatedPromptTokens} token; Context={_options.ContextLength}; NumPredict={_options.SceneNumPredict}", sceneNumber - 1, project.CalculatedClipCount, null, GenerationLogLevel.Information, sceneNumber, sceneNumber);

        var stopwatch = Stopwatch.StartNew();
        var response = await GenerateWithOneRepairAsync<SingleScenePackageResponse>(
            [new OllamaChatMessage("system", systemPrompt), new OllamaChatMessage("user", userPrompt)],
            StoryJsonSchemas.SingleScenePackageSchema(),
            progress,
            $"Sahne {sceneNumber}",
            cancellationToken,
            _options.SceneTextModel,
            new OllamaFailureContext(project.Id, sceneNumber, "SingleSceneGeneration"),
            candidate => ValidateSingleSceneResponse(candidate, sceneNumber, project.ClipDurationSeconds));
        stopwatch.Stop();
        Report(progress, $"Sahne {sceneNumber}", $"Qwen cevabi alindi. Sure={stopwatch.Elapsed:mm\\:ss}", sceneNumber - 1, project.CalculatedClipCount, null, GenerationLogLevel.Success, sceneNumber, sceneNumber);

        var scene = ConvertSingleScene(response);
        ValidateScenePackageBatch([scene], sceneNumber, sceneNumber, characters, project.ClipDurationSeconds);
        SanitizeSilentVideoPrompts([scene]);
        ValidateSilentVideoPrompts([scene]);
        Report(progress, $"Sahne {sceneNumber}", $"Sahne {sceneNumber} JSON ve alan dogrulamasi tamamlandi.", sceneNumber - 1, project.CalculatedClipCount, null, GenerationLogLevel.Success, sceneNumber, sceneNumber);
        return scene;
    }

    public static int EstimatePromptTokens(params string[] prompts)
    {
        var characterCount = prompts.Sum(prompt => prompt?.Length ?? 0);
        return Math.Max(1, (int)Math.Ceiling(characterCount / 4d));
    }

    private static IProgress<OllamaStreamProgress> CreateOllamaStreamProgress(
        IProgress<StoryGenerationProgress>? progress,
        string phase)
    {
        var lastChunkReport = DateTimeOffset.MinValue;
        return new InlineProgress<OllamaStreamProgress>(stream =>
        {
            string? message = stream.Stage switch
            {
                OllamaStreamStage.RequestStarted => $"Qwen 30B istegi gonderildi. Model={stream.Model}",
                OllamaStreamStage.ModelPreparing => "Model hazirlaniyor.",
                OllamaStreamStage.FirstContentChunk => $"Ilk yanit parcasi {stream.Elapsed.TotalSeconds:0.0} saniyede alindi.",
                OllamaStreamStage.ContentChunk when DateTimeOffset.UtcNow - lastChunkReport >= TimeSpan.FromSeconds(10) =>
                    $"Yanit devam ediyor. Parca={stream.ContentChunkCount}; Son aktivite={stream.TimeSinceLastActivity.TotalSeconds:0.0} saniye once.",
                OllamaStreamStage.ActivityHeartbeat =>
                    $"Yanit bekleniyor. Son aktivite={stream.TimeSinceLastActivity.TotalSeconds:0.0} saniye once.",
                OllamaStreamStage.Completed =>
                    $"Cevap tamamlandi. Done={stream.Done}; DoneReason={stream.DoneReason}; Karakter={stream.ResponseCharacterCount}; Baslangic={(stream.LoadDuration > TimeSpan.FromSeconds(2) ? "cold" : "warm")}; Load={stream.LoadDuration.TotalSeconds:0.00}s; Eval={stream.EvaluationDuration.TotalSeconds:0.00}s; PromptToken={stream.PromptTokenCount}; ResponseToken={stream.ResponseTokenCount}; Parca={stream.ContentChunkCount}; Toplam={stream.Elapsed.TotalSeconds:0.00}s.",
                OllamaStreamStage.JsonValidating => "JSON dogrulaniyor.",
                _ => null
            };

            if (message is null)
            {
                return;
            }

            if (stream.Stage == OllamaStreamStage.ContentChunk)
            {
                lastChunkReport = DateTimeOffset.UtcNow;
            }

            Report(progress, phase, message, 0, 0, null, GenerationLogLevel.Information);
        });
    }

    private static ScenePackageItemResponse ConvertSingleScene(SingleScenePackageResponse response)
    {
        var dialogue = ParseDialogueJson(response.DialogueJson);
        return new ScenePackageItemResponse
        {
            SceneNumber = response.SceneNumber,
            Title = response.Title,
            StoryBeat = response.StoryBeat,
            SceneDescription = response.SceneDescription,
            LocationDescription = response.LocationDescription,
            TimeOfDay = response.TimeOfDay,
            Characters = response.Characters,
            ContinuityFromPreviousScene = response.ContinuityFromPreviousScene,
            ImagePrompt = response.ImagePrompt,
            ImageNegativePrompt = SceneNegativePromptPolicy.SanitizeImage(response.ImageNegativePrompt),
            VideoPrompt = response.VideoPrompt,
            VideoNegativePrompt = SceneNegativePromptPolicy.SanitizeVideo(response.VideoNegativePrompt),
            NarrationText = string.Empty,
            Dialogue = dialogue,
            ValidationChecklist = response.ValidationChecklist
        };
    }

    private static List<DialogueLineResponse> ParseDialogueJson(string dialogueJson)
    {
        if (string.IsNullOrWhiteSpace(dialogueJson) || dialogueJson.Trim() == "[]")
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(dialogueJson);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => JsonSerializer.Deserialize<List<DialogueLineResponse>>(dialogueJson, JsonOptions)
                    ?? throw new InvalidOperationException("DialogueJson array deserialize edilemedi."),
                JsonValueKind.Object =>
                    [JsonSerializer.Deserialize<DialogueLineResponse>(dialogueJson, JsonOptions)
                        ?? throw new InvalidOperationException("DialogueJson object deserialize edilemedi.")],
                _ => throw new InvalidOperationException("DialogueJson root degeri object veya array olmali.")
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("DialogueJson gecerli JSON degil.", ex);
        }
    }

    private static void ValidateSingleSceneResponse(
        SingleScenePackageResponse response,
        int expectedSceneNumber,
        int expectedDurationSeconds)
    {
        var errors = new List<string>();
        NormalizeSingleSceneContinuity(response, expectedSceneNumber);
        if (response.SceneNumber != expectedSceneNumber) errors.Add($"sceneNumber expected {expectedSceneNumber}, actual {response.SceneNumber}");
        if (response.DurationSeconds != expectedDurationSeconds) errors.Add($"durationSeconds expected {expectedDurationSeconds}, actual {response.DurationSeconds}");
        AddRequired(response.Title, "title", errors);
        AddRequired(response.StoryBeat, "storyBeat", errors);
        AddRequired(response.SceneDescription, "sceneDescription", errors);
        AddRequired(response.LocationDescription, "locationDescription", errors);
        AddRequired(response.TimeOfDay, "timeOfDay", errors);
        AddRequired(response.ImagePrompt, "imagePrompt", errors);
        AddRequired(response.ImageNegativePrompt, "imageNegativePrompt", errors);
        AddRequired(response.VideoPrompt, "videoPrompt", errors);
        AddRequired(response.VideoNegativePrompt, "videoNegativePrompt", errors);
        if (expectedSceneNumber > 1)
        {
            AddRequired(response.ContinuityFromPreviousScene, "continuityFromPreviousScene", errors);
        }
        AddMaxLength(response.Title, "title", 120, errors);
        AddMaxLength(response.TimeOfDay, "timeOfDay", 120, errors);
        AddMaxLength(response.StoryBeat, "storyBeat", 900, errors);
        AddMaxLength(response.SceneDescription, "sceneDescription", 900, errors);
        AddMaxLength(response.LocationDescription, "locationDescription", 900, errors);
        AddMaxLength(response.ImagePrompt, "imagePrompt", 900, errors);
        AddMaxLength(response.ImageNegativePrompt, "imageNegativePrompt", 900, errors);
        AddMaxLength(response.VideoPrompt, "videoPrompt", 900, errors);
        AddMaxLength(response.VideoNegativePrompt, "videoNegativePrompt", 900, errors);
        AddMaxLength(response.NarrationText, "narrationText", 900, errors);
        AddMaxLength(response.DialogueJson, "dialogueJson", 900, errors);
        AddMaxLength(response.ContinuityFromPreviousScene, "continuityFromPreviousScene", 900, errors);
        if (response.Characters is null) errors.Add("characters is null");
        if (response.ValidationChecklist is null) errors.Add("validationChecklist is null");
        if (string.IsNullOrWhiteSpace(response.DialogueJson))
        {
            errors.Add("dialogueJson is empty");
        }
        else
        {
            try
            {
                _ = ParseDialogueJson(response.DialogueJson);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", errors));
        }
    }

    private static void NormalizeSingleSceneContinuity(SingleScenePackageResponse response, int expectedSceneNumber)
    {
        if (expectedSceneNumber == 1 && string.IsNullOrWhiteSpace(response.ContinuityFromPreviousScene))
        {
            response.ContinuityFromPreviousScene = OpeningSceneContinuityFromPreviousScene;
        }
    }

    private static void AddRequired(string? value, string fieldName, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{fieldName} is empty");
        }
    }

    private static void AddMaxLength(string? value, string fieldName, int maxLength, ICollection<string> errors)
    {
        if (value?.Length > maxLength)
        {
            errors.Add($"{fieldName} exceeds {maxLength} characters");
        }
    }

    private async Task<List<SceneOutlineItemDto>> GenerateOutlinesForRangeWithTimeoutRetryAsync(
        FilmProject project,
        FilmStory story,
        int start,
        int end,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var attemptStarted = DateTime.Now;
            try
            {
                var outlines = await GenerateOutlinesForRangeAsync(project, story, start, end, progress, cancellationToken);
                Report(progress, $"Sahne plani {start}-{end}", $"Sahne plani alindi. Deneme={attempt}; Gecen sure={(DateTime.Now - attemptStarted):mm\\:ss}", 0, project.CalculatedClipCount, null, GenerationLogLevel.Success, start, end);
                return outlines;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 1)
            {
                Report(progress, $"Sahne plani {start}-{end}", "Sahne plani istegi timeout oldu; bir kez daha deneniyor.", 0, project.CalculatedClipCount, null, GenerationLogLevel.Warning, start, end);
            }
            catch (TimeoutException) when (attempt == 1)
            {
                Report(progress, $"Sahne plani {start}-{end}", "Sahne plani istegi timeout oldu; bir kez daha deneniyor.", 0, project.CalculatedClipCount, null, GenerationLogLevel.Warning, start, end);
            }
        }

        throw new TimeoutException($"Sahne plani {start}-{end} timeout nedeniyle tamamlanamadi.");
    }

    private async Task<ScenePackageBatchResponse> GenerateScenePackageWithTimeoutRetryAsync(
        FilmProject project,
        IReadOnlyList<OllamaChatMessage> messages,
        int start,
        int end,
        IProgress<StoryGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var attemptStarted = DateTime.Now;
            try
            {
                var response = await GenerateWithOneRepairAsync<ScenePackageBatchResponse>(
                    messages,
                    StoryJsonSchemas.ScenePackageBatchSchema(),
                    progress,
                    $"Sahne paketi {start}-{end}",
                    cancellationToken,
                    gpuProjectId: project.Id,
                    gpuSceneId: start);
                Report(progress, $"Sahne paketi {start}-{end}", $"Qwen cevabi alindi. Deneme={attempt}; Gecen sure={(DateTime.Now - attemptStarted):mm\\:ss}", 0, project.CalculatedClipCount, null, GenerationLogLevel.Success, start, end);
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt == 1)
            {
                Report(progress, $"Sahne paketi {start}-{end}", "Paket istegi timeout oldu; bir kez daha deneniyor.", 0, project.CalculatedClipCount, null, GenerationLogLevel.Warning, start, end);
            }
            catch (TimeoutException) when (attempt == 1)
            {
                Report(progress, $"Sahne paketi {start}-{end}", "Paket istegi timeout oldu; bir kez daha deneniyor.", 0, project.CalculatedClipCount, null, GenerationLogLevel.Warning, start, end);
            }
        }

        throw new TimeoutException($"Sahne paketi {start}-{end} timeout nedeniyle tamamlanamadi.");
    }

    private async Task<FilmStory> LoadStorySnapshotAsync(int filmStoryId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmStories
            .AsNoTracking()
            .Include(story => story.Characters)
            .FirstAsync(story => story.Id == filmStoryId, cancellationToken);
    }

    private async Task<List<StoryCharacter>> LoadStoryCharactersAsync(int filmStoryId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.StoryCharacters
            .AsNoTracking()
            .Where(item => item.FilmStoryId == filmStoryId)
            .OrderBy(item => item.SortOrder)
            .ToListAsync(cancellationToken);
    }

    private async Task<string> BuildPreviousSceneContextAsync(int filmProjectId, int startScene, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var previous = await db.FilmScenes
            .AsNoTracking()
            .Where(item => item.FilmProjectId == filmProjectId && item.SceneNumber < startScene)
            .OrderByDescending(item => item.SceneNumber)
            .Take(1)
            .OrderBy(item => item.SceneNumber)
            .Select(item => $"{item.SceneNumber}. {item.Title} | {item.StoryBeat} | continuity: {item.ContinuityFromPreviousScene}")
            .ToListAsync(cancellationToken);
        return string.Join(Environment.NewLine, previous);
    }

    private async Task<bool> SceneExistsAsync(int filmProjectId, int sceneNumber, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmScenes.AsNoTracking().AnyAsync(item => item.FilmProjectId == filmProjectId && item.SceneNumber == sceneNumber, cancellationToken);
    }

    private async Task<HashSet<int>> SaveSceneBatchAsync(
        FilmProject project,
        int filmStoryId,
        IReadOnlyList<ScenePackageItemResponse> scenes,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var sceneNumbers = scenes.Select(scene => scene.SceneNumber).ToList();
        var existingSceneNumbers = await db.FilmScenes
            .Where(scene => scene.FilmProjectId == project.Id && sceneNumbers.Contains(scene.SceneNumber))
            .Select(scene => scene.SceneNumber)
            .ToListAsync(cancellationToken);
        var existingSet = existingSceneNumbers.ToHashSet();
        var inserted = new HashSet<int>();

        foreach (var scene in scenes)
        {
            if (existingSet.Contains(scene.SceneNumber))
            {
                continue;
            }

            db.FilmScenes.Add(new FilmScene
            {
                FilmProjectId = project.Id,
                FilmStoryId = filmStoryId,
                SceneNumber = scene.SceneNumber,
                DurationSeconds = project.ClipDurationSeconds,
                Title = scene.Title.Trim(),
                StoryBeat = scene.StoryBeat.Trim(),
                SceneDescription = scene.SceneDescription.Trim(),
                LocationDescription = scene.LocationDescription.Trim(),
                TimeOfDay = scene.TimeOfDay.Trim(),
                CharactersJson = JsonSerializer.Serialize(scene.Characters, JsonOptions),
                ContinuityFromPreviousScene = scene.ContinuityFromPreviousScene.Trim(),
                ImagePrompt = scene.ImagePrompt.Trim(),
                ImageNegativePrompt = scene.ImageNegativePrompt.Trim(),
                VideoPrompt = scene.VideoPrompt.Trim(),
                VideoNegativePrompt = scene.VideoNegativePrompt.Trim(),
                NarrationText = string.Empty,
                DialogueJson = JsonSerializer.Serialize(scene.Dialogue, JsonOptions),
                ValidationChecklistJson = JsonSerializer.Serialize(scene.ValidationChecklist, JsonOptions),
                Status = FilmSceneStatus.PromptReady,
                CreatedAt = DateTime.Now
            });
            inserted.Add(scene.SceneNumber);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }
        catch (DbUpdateException ex) when (IsFilmSceneProjectSceneNumberUniqueViolation(ex))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await using var reloadDb = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var concurrentlyCompleted = await reloadDb.FilmScenes
                .AsNoTracking()
                .Where(scene => scene.FilmProjectId == project.Id && sceneNumbers.Contains(scene.SceneNumber))
                .Select(scene => scene.SceneNumber)
                .ToListAsync(cancellationToken);
            var completion = ClassifyConcurrentSceneCompletion(sceneNumbers, concurrentlyCompleted);
            if (completion.Count == 0)
            {
                throw;
            }

            _logger.LogInformation(
                "Concurrent scene completion detected after unique constraint violation. FilmProjectId={FilmProjectId}; SceneNumbers={SceneNumbers}",
                project.Id,
                string.Join(',', completion.OrderBy(item => item)));
            return completion;
        }
    }

    internal static bool IsFilmSceneProjectSceneNumberUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException &&
                IsFilmSceneProjectSceneNumberUniqueViolation(sqlException.Number, sqlException.Message))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsFilmSceneProjectSceneNumberUniqueViolation(int sqlErrorNumber, string message) =>
        sqlErrorNumber is 2601 or 2627 &&
        message.Contains("IX_FilmScenes_FilmProjectId_SceneNumber", StringComparison.OrdinalIgnoreCase);

    internal static HashSet<int> ClassifyConcurrentSceneCompletion(
        IEnumerable<int> requestedSceneNumbers,
        IEnumerable<int> reloadedSceneNumbers) =>
        requestedSceneNumbers.Intersect(reloadedSceneNumbers).ToHashSet();

    private async Task UpdateProjectStatusAsync(int filmProjectId, FilmProjectStatus status, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var project = await db.FilmProjects.FirstOrDefaultAsync(item => item.Id == filmProjectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        project.Status = status;
        project.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedIfPossibleAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        try
        {
            await UpdateProjectStatusAsync(filmProjectId, FilmProjectStatus.Failed, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FilmProject status Failed olarak guncellenemedi. FilmProjectId: {FilmProjectId}", filmProjectId);
        }
    }

    private OllamaGenerationSettings CreateStageGenerationSettings(int filmProjectId, int sceneNumber, string operationName, int numPredict)
    {
        var settings = CreateBaseGenerationSettings(new OllamaFailureContext(filmProjectId, sceneNumber, operationName));
        settings.Temperature = _options.SceneStructuredTemperature;
        settings.TopP = _options.SceneStructuredTopP;
        settings.TopK = _options.SceneStructuredTopK;
        settings.RepeatPenalty = _options.SceneStructuredRepeatPenalty;
        settings.RepeatLastN = _options.SceneStructuredRepeatLastN;
        settings.NumPredict = numPredict;
        return settings;
    }

    private int SelectStageNumPredict(int minimum, int maximum) =>
        Math.Clamp(_options.SceneNumPredict, minimum, maximum);

    private static double MapProgress(double from, double to, int completed, int total)
    {
        if (total <= 0)
        {
            return from;
        }

        return from + (to - from) * Math.Clamp(completed / (double)total, 0, 1);
    }

    private static StoryGenerationProgressResult ToProgressResult(int filmProjectId, FilmStory story, int generatedSceneCount) =>
        new()
        {
            FilmProjectId = filmProjectId,
            FilmStoryId = story.Id,
            Title = story.Title,
            GeneratedSceneCount = generatedSceneCount
        };

    private static StoryBibleResponse ToStoryBible(FilmStory story, StoryCharactersResponse characters) =>
        new()
        {
            Title = story.Title,
            Logline = story.Logline,
            Synopsis = story.Synopsis,
            OpeningSummary = story.OpeningSummary,
            DevelopmentSummary = story.DevelopmentSummary,
            ClimaxSummary = story.ClimaxSummary,
            EndingSummary = story.EndingSummary,
            WorldDescription = story.WorldDescription,
            VisualDirection = story.VisualDirection,
            ContinuityRules = DeserializeStringArray(story.ContinuityRulesJson),
            Characters = characters.Characters
        };

    private static List<string> DeserializeStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void ValidateStoryNarrative(StoryNarrativeResponse narrative)
    {
        AddRequiredOrThrow(narrative.Title, "title");
        AddRequiredOrThrow(narrative.Logline, "logline");
        AddRequiredOrThrow(narrative.Synopsis, "synopsis");
        AddRequiredOrThrow(narrative.OpeningSummary, "openingSummary");
        AddRequiredOrThrow(narrative.DevelopmentSummary, "developmentSummary");
        AddRequiredOrThrow(narrative.ClimaxSummary, "climaxSummary");
        AddRequiredOrThrow(narrative.EndingSummary, "endingSummary");
        AddRequiredOrThrow(narrative.WorldDescription, "worldDescription");
        AddRequiredOrThrow(narrative.VisualDirection, "visualDirection");
        if (narrative.ContinuityRules is null)
        {
            throw new InvalidOperationException("continuityRules is null");
        }
    }

    private static void ValidateStoryCharacters(FilmStory story, StoryCharactersResponse response)
    {
        if (response.Characters is null)
        {
            throw new InvalidOperationException("characters is null");
        }

        StoryCharacterFieldValidator.Validate(ToStoryBible(story, response));
    }

    private static void ValidateStoryCharactersContainer(StoryCharactersResponse response)
    {
        if (response.Characters is null)
        {
            throw new InvalidOperationException("characters is null");
        }
    }

    private static void ValidateCharacterCorrections(StoryCharacterCorrectionsResponse response)
    {
        if (response.Corrections is null)
        {
            throw new InvalidOperationException("corrections is null");
        }

        foreach (var correction in response.Corrections)
        {
            AddRequiredOrThrow(correction.CharacterKey, "characterKey");
            AddRequiredOrThrow(correction.Field, "field");
            AddRequiredOrThrow(correction.Value, "value");
        }
    }

    private static void ValidateNarrativeSceneResponse(NarrativeSceneResponse response, int expectedSceneNumber, int expectedDurationSeconds)
    {
        NormalizeNarrativeSceneContinuity(response, expectedSceneNumber);
        var errors = new List<string>();
        if (response.SceneNumber != expectedSceneNumber) errors.Add($"sceneNumber expected {expectedSceneNumber}, actual {response.SceneNumber}");
        if (response.DurationSeconds != expectedDurationSeconds) errors.Add($"durationSeconds expected {expectedDurationSeconds}, actual {response.DurationSeconds}");
        AddRequired(response.Title, "title", errors);
        AddRequired(response.StoryBeat, "storyBeat", errors);
        AddRequired(response.SceneDescription, "sceneDescription", errors);
        AddRequired(response.LocationDescription, "locationDescription", errors);
        AddRequired(response.TimeOfDay, "timeOfDay", errors);
        AddRequired(response.DialogueIntent, "dialogueIntent", errors);
        if (response.Characters is null) errors.Add("characters is null");
        if (expectedSceneNumber == 1 && response.ContinuityFromPreviousScene != OpeningSceneContinuityFromPreviousScene)
        {
            errors.Add($"continuityFromPreviousScene must be exactly {OpeningSceneContinuityFromPreviousScene}");
        }
        if (expectedSceneNumber > 1)
        {
            AddRequired(response.ContinuityFromPreviousScene, "continuityFromPreviousScene", errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" | ", errors));
        }
    }

    private static void ValidateImagePromptResponse(SceneImagePromptResponse response, int expectedSceneNumber)
    {
        if (response.SceneNumber != expectedSceneNumber)
        {
            throw new InvalidOperationException($"sceneNumber expected {expectedSceneNumber}, actual {response.SceneNumber}");
        }

        AddRequiredOrThrow(response.ImagePrompt, "imagePrompt");
        AddRequiredOrThrow(response.ImageNegativePrompt, "imageNegativePrompt");
    }

    private static void ValidateVideoPromptResponse(SceneVideoPromptResponse response, int expectedSceneNumber)
    {
        if (response.SceneNumber != expectedSceneNumber)
        {
            throw new InvalidOperationException($"sceneNumber expected {expectedSceneNumber}, actual {response.SceneNumber}");
        }

        AddRequiredOrThrow(response.VideoPrompt, "videoPrompt");
        AddRequiredOrThrow(response.VideoNegativePrompt, "videoNegativePrompt");
        AddRequiredOrThrow(response.StartState, "startState");
        AddRequiredOrThrow(response.MotionPlan, "motionPlan");
        AddRequiredOrThrow(response.EndState, "endState");
    }

    private static void NormalizeNarrativeSceneContinuity(NarrativeSceneResponse response, int expectedSceneNumber)
    {
        if (expectedSceneNumber == 1 && string.IsNullOrWhiteSpace(response.ContinuityFromPreviousScene))
        {
            response.ContinuityFromPreviousScene = OpeningSceneContinuityFromPreviousScene;
        }
    }

    private static void AddRequiredOrThrow(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{fieldName} is empty");
        }
    }

    internal static void ValidateStoryBible(StoryBibleResponse bible)
    {
        if (string.IsNullOrWhiteSpace(bible.Title))
        {
            throw new InvalidOperationException("Story Bible beklenen zorunlu alanlari icermiyor.");
        }

        var duplicateCharacter = bible.Characters
            .GroupBy(character => character.CharacterKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicateCharacter is not null)
        {
            throw new InvalidOperationException($"Tekrarlanan karakter anahtari uretildi: {duplicateCharacter.Key}");
        }

        StoryCharacterFieldValidator.Validate(bible);
    }

    private static void ValidateSceneNumbers(IEnumerable<int> sceneNumbers, int start, int end, string label)
    {
        var numbers = sceneNumbers.OrderBy(number => number).ToList();
        var expected = Enumerable.Range(start, end - start + 1).ToList();
        if (!numbers.SequenceEqual(expected))
        {
            throw new InvalidOperationException($"{label} beklenen sahne numaralarini uretmedi. Beklenen: {start}-{end}.");
        }
    }

    private static void ValidateScenePackageBatch(
        IReadOnlyList<ScenePackageItemResponse> scenes,
        int start,
        int end,
        IReadOnlyList<StoryCharacter> characters,
        int durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            throw new InvalidOperationException("Sahne paketleri pozitif klip suresi icin uretilmelidir.");
        }

        if (scenes.Count != end - start + 1)
        {
            throw new InvalidOperationException($"Sahne paketi {start}-{end} beklenen {end - start + 1} sahneyi dondurmedi.");
        }

        ValidateSceneNumbers(scenes.Select(scene => scene.SceneNumber), start, end, "Sahne paketi");
        var characterKeys = characters.Select(item => item.CharacterKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var scene in scenes)
        {
            NormalizeScenePackageContinuity(scene);
            if (string.IsNullOrWhiteSpace(scene.ImagePrompt))
            {
                throw new InvalidOperationException($"{scene.SceneNumber}. sahnenin ImagePrompt alani bos.");
            }

            if (string.IsNullOrWhiteSpace(scene.VideoPrompt))
            {
                throw new InvalidOperationException($"{scene.SceneNumber}. sahnenin VideoPrompt alani bos.");
            }

            if (scene.SceneNumber > 1 && string.IsNullOrWhiteSpace(scene.ContinuityFromPreviousScene))
            {
                throw new InvalidOperationException($"{scene.SceneNumber}. sahnenin continuity alani bos.");
            }

            foreach (var line in scene.Dialogue)
            {
                if (!string.IsNullOrWhiteSpace(line.CharacterKey) && !characterKeys.Contains(line.CharacterKey))
                {
                    throw new InvalidOperationException($"{scene.SceneNumber}. sahnede StoryCharacter ile eslesmeyen speakerKey var.");
                }
            }
        }
    }

    private static void NormalizeScenePackageContinuity(ScenePackageItemResponse scene)
    {
        if (scene.SceneNumber == 1 && string.IsNullOrWhiteSpace(scene.ContinuityFromPreviousScene))
        {
            scene.ContinuityFromPreviousScene = OpeningSceneContinuityFromPreviousScene;
        }
    }

    public static int FindFirstMissingScene(IReadOnlySet<int> existingSceneNumbers, int totalSceneCount)
    {
        for (var sceneNumber = 1; sceneNumber <= totalSceneCount; sceneNumber++)
        {
            if (!existingSceneNumbers.Contains(sceneNumber))
            {
                return sceneNumber;
            }
        }

        return totalSceneCount + 1;
    }

    public static int GetBatchStart(int sceneNumber)
    {
        return ((Math.Max(1, sceneNumber) - 1) / ResumeSceneBatchSize) * ResumeSceneBatchSize + 1;
    }

    public static string? TryGetCompletionError(IReadOnlySet<int> sceneNumbers, int totalDurationSeconds, int totalSceneCount, int clipDurationSeconds)
    {
        var expected = Enumerable.Range(1, totalSceneCount).ToHashSet();
        if (sceneNumbers.Count != totalSceneCount || !expected.SetEquals(sceneNumbers))
        {
            return "FilmScenes 1-30 arasi eksiksiz degil.";
        }

        var expectedDuration = totalSceneCount * clipDurationSeconds;
        if (totalDurationSeconds != expectedDuration)
        {
            return $"Toplam sahne suresi beklenen degerde degil. Beklenen={expectedDuration}; Gercek={totalDurationSeconds}.";
        }

        return null;
    }

    private static double ProgressFor(int sceneCount, int totalSceneCount)
    {
        return totalSceneCount <= 0 ? 0 : Math.Min(99, 10 + sceneCount * 85d / totalSceneCount);
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static void ValidateSilentVideoPrompts(IEnumerable<ScenePackageItemResponse> scenes)
    {
        foreach (var scene in scenes)
        {
            ValidateSilentVideoPromptFields(scene.SceneNumber, scene.VideoPrompt, scene.VideoNegativePrompt);
        }
    }

    internal static void ValidateSilentVideoPromptFields(int sceneNumber, string? videoPrompt, string? videoNegativePrompt)
    {
        if (TryFindForbiddenSilentVideoInstruction(videoPrompt, isNegativePrompt: false, out var forbiddenTerm))
        {
            throw new InvalidOperationException($"{sceneNumber}. sahnenin videoPrompt alaninda sessiz video kuralina aykiri ifade bulundu: {forbiddenTerm}");
        }

        if (TryFindForbiddenSilentVideoInstruction(videoNegativePrompt, isNegativePrompt: true, out forbiddenTerm))
        {
            throw new InvalidOperationException($"{sceneNumber}. sahnenin videoNegativePrompt alaninda sessiz video kuralina aykiri ifade bulundu: {forbiddenTerm}");
        }
    }

    internal static bool HasInvalidSilentVideoPromptFields(string? videoPrompt, string? videoNegativePrompt) =>
        TryFindForbiddenSilentVideoInstruction(videoPrompt, isNegativePrompt: false, out _) ||
        TryFindForbiddenSilentVideoInstruction(videoNegativePrompt, isNegativePrompt: true, out _);

    private static void SanitizeSilentVideoPrompts(IEnumerable<ScenePackageItemResponse> scenes)
    {
        foreach (var scene in scenes)
        {
            scene.VideoPrompt = RemoveForbiddenVideoPromptSentences(scene.VideoPrompt);
            scene.ImageNegativePrompt = SceneNegativePromptPolicy.SanitizeImage(scene.ImageNegativePrompt);
            scene.VideoNegativePrompt = SceneNegativePromptPolicy.SanitizeVideo(scene.VideoNegativePrompt);
            if (string.IsNullOrWhiteSpace(scene.VideoPrompt))
            {
                scene.VideoPrompt = "The characters move with clear visible body language and facial expressions while the camera moves slowly through the established composition. Preserve the exact character identities, clothing, proportions, lighting, background layout and visual continuity. No scene transition, no sudden motion, no new objects.";
            }
        }
    }

    private static string RemoveForbiddenVideoPromptSentences(string prompt)
    {
        var parts = prompt
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Split(new[] { '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var allowedParts = parts
            .Where(part => !TryFindForbiddenSilentVideoInstruction(part, isNegativePrompt: false, out _))
            .ToList();

        return string.Join(". ", allowedParts).Trim();
    }

    internal static bool TryFindForbiddenSilentVideoInstruction(string? prompt, bool isNegativePrompt, out string forbiddenTerm)
    {
        forbiddenTerm = string.Empty;
        var tokens = TokenizeForValidation(prompt);
        if (tokens.Count == 0)
        {
            return false;
        }

        foreach (var term in VideoPromptAudioTerms)
        {
            var termTokens = TokenizeForValidation(term);
            if (termTokens.Count == 0)
            {
                continue;
            }

            for (var index = 0; index <= tokens.Count - termTokens.Count; index++)
            {
                if (!MatchesForbiddenAudioTerm(tokens, index, termTokens))
                {
                    continue;
                }

                if (isNegativePrompt && IsNegativeAudioBlocker(tokens, index))
                {
                    continue;
                }

                forbiddenTerm = term;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> TokenizeForValidation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var decomposed = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var tokens = new List<string>();
        var builder = new System.Text.StringBuilder();
        foreach (var raw in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var ch = raw switch
            {
                'ı' or 'İ' => 'i',
                'ğ' => 'g',
                'ü' => 'u',
                'ş' => 's',
                'ö' => 'o',
                'ç' => 'c',
                _ => raw
            };

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                continue;
            }

            FlushToken(builder, tokens);
        }

        FlushToken(builder, tokens);
        return tokens;
    }

    private static void FlushToken(System.Text.StringBuilder builder, List<string> tokens)
    {
        if (builder.Length == 0)
        {
            return;
        }

        tokens.Add(builder.ToString());
        builder.Clear();
    }

    private static bool MatchesForbiddenAudioTerm(IReadOnlyList<string> tokens, int startIndex, IReadOnlyList<string> termTokens)
    {
        if (termTokens.Count == 1 && termTokens[0] == "ses")
        {
            return IsTurkishSoundToken(tokens[startIndex]);
        }

        for (var offset = 0; offset < termTokens.Count; offset++)
        {
            if (!MatchesForbiddenToken(tokens[startIndex + offset], termTokens[offset]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesForbiddenToken(string token, string term) =>
        term switch
        {
            "ses" => IsTurkishSoundToken(token),
            "muzik" => token == "muzik" || token.StartsWith("muzig", StringComparison.Ordinal) || token.StartsWith("muzik", StringComparison.Ordinal),
            "konusma" => token == "konusma" || token.StartsWith("konusmalar", StringComparison.Ordinal) || token.StartsWith("konusmay", StringComparison.Ordinal),
            "diyalog" => token == "diyalog" || token.StartsWith("diyalogl", StringComparison.Ordinal) || token.StartsWith("diyalogu", StringComparison.Ordinal),
            "anlatici" => token == "anlatici" || token.StartsWith("anlaticin", StringComparison.Ordinal),
            "narration" => token == "narration",
            "narrator" => token == "narrator",
            "voice" => token == "voice" || token.StartsWith("voiceover", StringComparison.Ordinal),
            "spoken" => token == "spoken",
            "sound" => token == "sound",
            _ => token == term
        };

    private static bool IsTurkishSoundToken(string token) =>
        token == "ses" ||
        token.StartsWith("sesi", StringComparison.Ordinal) ||
        token.StartsWith("sesin", StringComparison.Ordinal) ||
        token.StartsWith("sese", StringComparison.Ordinal) ||
        token.StartsWith("sesten", StringComparison.Ordinal) ||
        token.StartsWith("sesle", StringComparison.Ordinal) ||
        token.StartsWith("sesler", StringComparison.Ordinal) ||
        token.StartsWith("sesli", StringComparison.Ordinal);

    private static bool IsNegativeAudioBlocker(IReadOnlyList<string> tokens, int audioTermIndex)
    {
        var windowStart = Math.Max(0, audioTermIndex - 3);
        for (var index = windowStart; index < audioTermIndex; index++)
        {
            if (tokens[index] is "no" or "without" or "avoid" or "exclude" or "remove" or "muted" or "mute" or "silent" or "sessiz")
            {
                return true;
            }
        }

        return audioTermIndex == 0 && (tokens[0] is "silent" or "sessiz");
    }

    private static string NormalizeForValidation(string value)
    {
        return value
            .ToLowerInvariant()
            .Replace("ı", "i")
            .Replace("ğ", "g")
            .Replace("ü", "u")
            .Replace("ş", "s")
            .Replace("ö", "o")
            .Replace("ç", "c");
    }

    private static string? BuildPreviousOutlineContext(IReadOnlyList<SceneOutlineItemDto> outlines)
    {
        if (outlines.Count == 0)
        {
            return null;
        }

        return string.Join(Environment.NewLine, outlines.TakeLast(3).Select(scene => $"{scene.SceneNumber}. {scene.Title}: {scene.ShortDescription}"));
    }

    private static void Report(
        IProgress<StoryGenerationProgress>? progress,
        string phase,
        string message,
        int completed,
        int total,
        double? percentage,
        GenerationLogLevel level,
        int? sceneStart = null,
        int? sceneEnd = null)
    {
        progress?.Report(new StoryGenerationProgress
        {
            Phase = phase,
            Message = message,
            CompletedItems = completed,
            TotalItems = total,
            Percentage = percentage ?? (total <= 0 ? 0 : Math.Round(completed * 100d / total, 1)),
            Level = level,
            Timestamp = DateTime.Now,
            SceneStart = sceneStart,
            SceneEnd = sceneEnd
        });
    }
}

internal sealed record StoryResumeState(
    FilmStory? Story,
    int CharacterCount,
    HashSet<int> SceneNumbers,
    int TotalDurationSeconds,
    int DuplicateSceneGroups);

internal enum StoryBibleOutputProfile
{
    Detailed,
    BriefVisual
}

internal enum StoryBibleGenerationAttempt
{
    Initial,
    FreshRetry,
    CharacterRepair
}

internal sealed record StoryBibleOutputBudget(
    StoryBibleOutputProfile Profile,
    StoryBibleGenerationAttempt Attempt,
    int NumPredict,
    int EstimatedPromptTokens,
    int ContextLength,
    int ContextMarginTokens,
    int DesiredNumPredict,
    int CappedMaximum);
