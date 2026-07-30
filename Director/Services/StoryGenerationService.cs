using System.Collections.Concurrent;
using System.Text.Json;
using Director.Data;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Models;
using Director.Ollama;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class StoryGenerationService : IStoryGenerationService
{
    private const int OutlineBlockSize = 25;
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ProjectLocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] VideoPromptAudioTerms =
    {
        "audio", "sound", "music", "voice", "narration", "narrator", "dialogue", "spoken", "ambient", "lip-sync",
        "sfx", "song", "muzik", "ses", "diyalog", "anlatici", "konusma"
    };

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOllamaClient _ollamaClient;
    private readonly IStoryPromptBuilder _promptBuilder;
    private readonly OllamaOptions _options;
    private readonly ILogger<StoryGenerationService> _logger;

    public StoryGenerationService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        IStoryPromptBuilder promptBuilder,
        IOptions<OllamaOptions> options,
        ILogger<StoryGenerationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _ollamaClient = ollamaClient;
        _promptBuilder = promptBuilder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<StoryGenerationProgressResult> GenerateStoryAsync(
        int filmProjectId,
        IProgress<StoryGenerationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var projectLock = ProjectLocks.GetOrAdd(filmProjectId, _ => new SemaphoreSlim(1, 1));
        if (!await projectLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("Bu proje icin hikaye uretimi zaten calisiyor.");
        }

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

            Report(progress, "Ollama kontrolu", $"{_options.Model} modeli kontrol ediliyor.", 0, project.CalculatedClipCount, 4, GenerationLogLevel.Information);
            await _ollamaClient.IsModelAvailableAsync(_options.Model, cancellationToken);
            Report(progress, "Ollama kontrolu", $"{_options.Model} modeli bulundu.", 0, project.CalculatedClipCount, 5, GenerationLogLevel.Success);

            Report(progress, "Film omurgasi", "Film omurgasi icin istek hazirlaniyor.", 0, project.CalculatedClipCount, 8, GenerationLogLevel.Information);
            var bible = await GenerateWithOneRepairAsync<StoryBibleResponse>(
                new[]
                {
                    new OllamaChatMessage("system", _promptBuilder.BuildStoryBibleSystemPrompt()),
                    new OllamaChatMessage("user", _promptBuilder.BuildStoryBibleUserPrompt(project))
                },
                StoryJsonSchemas.StoryBibleSchema(),
                progress,
                "Film omurgasi",
                cancellationToken);

            Report(progress, "Film omurgasi", "JSON semasi dogrulaniyor.", 0, project.CalculatedClipCount, 18, GenerationLogLevel.Information);
            ValidateStoryBible(bible);
            var story = await SaveStoryBibleAsync(project.Id, bible, cancellationToken);
            Report(progress, "Film omurgasi", $"{bible.Characters.Count} karakter ve film omurgasi SQL'e kaydedildi.", 0, project.CalculatedClipCount, 20, GenerationLogLevel.Success);

            var outlines = await GenerateOutlinesAsync(project, story, progress, cancellationToken);
            ValidateSceneNumbers(outlines.Select(scene => scene.SceneNumber), 1, project.CalculatedClipCount, "Sahne plani");

            await GenerateAndSaveScenePackagesAsync(project, story.Id, outlines, progress, cancellationToken);

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
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Hikaye uretimi iptal edildi. FilmProjectId: {FilmProjectId}", filmProjectId);
            await MarkFailedIfPossibleAsync(filmProjectId, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hikaye uretimi basarisiz oldu. FilmProjectId: {FilmProjectId}", filmProjectId);
            await MarkFailedIfPossibleAsync(filmProjectId, CancellationToken.None);
            throw;
        }
        finally
        {
            projectLock.Release();
        }
    }

    private async Task<FilmProject> LoadProjectSnapshotAsync(int filmProjectId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == filmProjectId, cancellationToken)
            ?? throw new InvalidOperationException("Film projesi bulunamadi.");
    }

    private async Task<T> GenerateWithOneRepairAsync<T>(
        IReadOnlyList<OllamaChatMessage> messages,
        object schema,
        IProgress<StoryGenerationProgress>? progress,
        string phase,
        CancellationToken cancellationToken)
    {
        try
        {
            Report(progress, phase, "Ollama istegi gonderildi, response bekleniyor.", 0, 0, null, GenerationLogLevel.Information);
            var result = await _ollamaClient.ChatStructuredAsync<T>(messages, schema, cancellationToken);
            Report(progress, phase, "Ollama response alindi ve deserialize edildi.", 0, 0, null, GenerationLogLevel.Success);
            return result;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("JSON", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(ex, "JSON parse basarisiz oldu. Tek repair denemesi yapilacak.");
            Report(progress, phase, "JSON parse basarisiz oldu; tek repair denemesi yapiliyor.", 0, 0, null, GenerationLogLevel.Warning);
            var repairMessages = messages
                .Concat(new[]
                {
                    new OllamaChatMessage("user", "Your previous response could not be parsed as the requested JSON schema. Return the same content again as strict JSON only, without markdown, comments or extra text.")
                })
                .ToList();

            return await _ollamaClient.ChatStructuredAsync<T>(repairMessages, schema, cancellationToken);
        }
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
                cancellationToken);

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
                cancellationToken);

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
                    cancellationToken);

                ValidateSceneNumbers(response.Scenes.Select(scene => scene.SceneNumber), start, end, "Sahne paketi repair blogu");
                SanitizeSilentVideoPrompts(response.Scenes);
                ValidateSilentVideoPrompts(response.Scenes);
            }

            await SaveSceneBatchAsync(project, filmStoryId, response.Scenes.OrderBy(scene => scene.SceneNumber).ToList(), cancellationToken);
            completed += batch.Count;
            Report(progress, "Veritabani", $"{start}-{end}. sahneler SQL'e kaydedildi.", completed, project.CalculatedClipCount, 35 + batchNumber * 60d / Math.Max(1, totalBatches), GenerationLogLevel.Success, start, end);
        }
    }

    private async Task<FilmStory> LoadStorySnapshotAsync(int filmStoryId, CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.FilmStories
            .AsNoTracking()
            .FirstAsync(story => story.Id == filmStoryId, cancellationToken);
    }

    private async Task SaveSceneBatchAsync(
        FilmProject project,
        int filmStoryId,
        IReadOnlyList<ScenePackageItemResponse> scenes,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var sceneNumbers = scenes.Select(scene => scene.SceneNumber).ToList();
        var existingScenes = await db.FilmScenes
            .Where(scene => scene.FilmProjectId == project.Id && sceneNumbers.Contains(scene.SceneNumber))
            .ToListAsync(cancellationToken);

        if (existingScenes.Count > 0)
        {
            db.FilmScenes.RemoveRange(existingScenes);
            await db.SaveChangesAsync(cancellationToken);
        }

        foreach (var scene in scenes)
        {
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
                NarrationText = scene.NarrationText.Trim(),
                DialogueJson = JsonSerializer.Serialize(scene.Dialogue, JsonOptions),
                ValidationChecklistJson = JsonSerializer.Serialize(scene.ValidationChecklist, JsonOptions),
                Status = FilmSceneStatus.PromptReady,
                CreatedAt = DateTime.Now
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

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

    private static void ValidateStoryBible(StoryBibleResponse bible)
    {
        if (string.IsNullOrWhiteSpace(bible.Title) || bible.Characters.Count == 0)
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

    private static void ValidateSilentVideoPrompts(IEnumerable<ScenePackageItemResponse> scenes)
    {
        foreach (var scene in scenes)
        {
            var videoPrompt = NormalizeForValidation(scene.VideoPrompt);
            var forbiddenTerm = VideoPromptAudioTerms.FirstOrDefault(term => videoPrompt.Contains(NormalizeForValidation(term), StringComparison.OrdinalIgnoreCase));
            if (forbiddenTerm is not null)
            {
                throw new InvalidOperationException($"{scene.SceneNumber}. sahnenin videoPrompt alaninda sessiz video kuralina aykiri ifade bulundu: {forbiddenTerm}");
            }
        }
    }

    private static void SanitizeSilentVideoPrompts(IEnumerable<ScenePackageItemResponse> scenes)
    {
        foreach (var scene in scenes)
        {
            scene.VideoPrompt = RemoveForbiddenVideoPromptSentences(scene.VideoPrompt);
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
            .Where(part => !VideoPromptAudioTerms.Any(term => NormalizeForValidation(part).Contains(NormalizeForValidation(term), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return string.Join(". ", allowedParts).Trim();
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
