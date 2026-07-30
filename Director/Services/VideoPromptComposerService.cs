using System.IO;
using System.Security.Cryptography;
using System.Text;
using Director.Data;
using Director.Dtos.MediaGeneration;
using Director.Enums;
using Director.Ollama;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class VideoPromptComposerService : IVideoPromptComposerService
{
    private static readonly HashSet<string> SupportedImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IOllamaClient _ollamaClient;
    private readonly ILogger<VideoPromptComposerService> _logger;

    public VideoPromptComposerService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IOllamaClient ollamaClient,
        ILogger<VideoPromptComposerService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _ollamaClient = ollamaClient;
        _logger = logger;
    }

    public async Task<VideoPromptCompositionRequest> BuildRequestAsync(int sceneId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var scene = await db.FilmScenes.AsNoTracking()
            .Include(item => item.FilmProject).ThenInclude(project => project.Story).ThenInclude(story => story!.Characters)
            .Include(item => item.MediaAssets)
            .FirstOrDefaultAsync(item => item.Id == sceneId, cancellationToken)
            ?? throw new InvalidOperationException("Sahne bulunamadi.");

        var reference = scene.MediaAssets
            .Where(asset => asset.MediaType == MediaType.Image && asset.IsSelected)
            .OrderByDescending(asset => asset.CreatedAt)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Once bu sahne icin ana referans gorsel secin.");

        ValidateReferenceImage(reference.FilePath);

        var previous = await db.FilmScenes.AsNoTracking()
            .Where(item => item.FilmProjectId == scene.FilmProjectId && item.SceneNumber == scene.SceneNumber - 1)
            .FirstOrDefaultAsync(cancellationToken);
        var next = await db.FilmScenes.AsNoTracking()
            .Where(item => item.FilmProjectId == scene.FilmProjectId && item.SceneNumber == scene.SceneNumber + 1)
            .FirstOrDefaultAsync(cancellationToken);

        var story = scene.FilmProject.Story;
        var characters = story?.Characters
            .OrderBy(character => character.SortOrder)
            .Select(character => $"{character.Name}: {Limit(character.PhysicalDescription + " " + character.ClothingDescription + " " + character.ContinuityDescription, 420)}")
            .ToList() ?? [];

        return new VideoPromptCompositionRequest
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            SceneNumber = scene.SceneNumber,
            ReferenceImagePath = reference.FilePath,
            ProjectName = scene.FilmProject.ProjectName,
            StoryTitle = story?.Title ?? string.Empty,
            Synopsis = Limit(story?.Synopsis ?? string.Empty, 1200),
            VisualDirection = Limit(story?.VisualDirection ?? string.Empty, 900),
            WorldDescription = Limit(story?.WorldDescription ?? string.Empty, 900),
            StoryGenre = scene.FilmProject.StoryGenre,
            VisualStyle = scene.FilmProject.VisualStyle,
            VideoStyle = scene.FilmProject.VideoStyle,
            Resolution = scene.FilmProject.Resolution,
            AspectRatio = scene.FilmProject.AspectRatio,
            ClipDurationSeconds = scene.FilmProject.ClipDurationSeconds,
            SceneTitle = scene.Title,
            StoryBeat = Limit(scene.StoryBeat, 900),
            SceneDescription = Limit(scene.SceneDescription, 900),
            ExistingVideoPrompt = Limit(scene.VideoPrompt, 900),
            ExistingVideoNegativePrompt = Limit(scene.VideoNegativePrompt, 600),
            ContinuityFromPreviousScene = Limit(scene.ContinuityFromPreviousScene, 600),
            PreviousSceneTitle = previous?.Title ?? string.Empty,
            PreviousSceneStoryBeat = Limit(previous?.StoryBeat ?? string.Empty, 500),
            PreviousSceneEndingContext = Limit(previous?.ContinuityFromPreviousScene ?? string.Empty, 500),
            NextSceneTitle = next?.Title ?? string.Empty,
            NextSceneStoryBeat = Limit(next?.StoryBeat ?? string.Empty, 500),
            Characters = Limit(string.Join(Environment.NewLine, characters), 1400),
            LocationDescription = Limit(scene.LocationDescription, 500),
            TimeOfDay = scene.TimeOfDay
        };
    }

    public async Task<VideoPromptCompositionResult> ComposeAsync(VideoPromptCompositionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateReferenceImage(request.ReferenceImagePath);
        var imageBytes = await File.ReadAllBytesAsync(request.ReferenceImagePath, cancellationToken);
        var imageBase64 = Convert.ToBase64String(imageBytes);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.SceneId + ":" + imageBytes.Length))).ToLowerInvariant();
        _logger.LogInformation("Preparing Qwen video prompt. SceneId={SceneId}; ImageBytes={ImageBytes}; ImageHash={ImageHash}", request.SceneId, imageBytes.Length, hash);

        var messages = new List<OllamaChatMessage>
        {
            new("system", BuildSystemPrompt()),
            new("user", BuildUserPrompt(request), [imageBase64])
        };

        var result = await _ollamaClient.ChatStructuredAsync<VideoPromptCompositionResult>(messages, BuildJsonSchema(), cancellationToken);
        ValidateResult(result);
        return result;
    }

    private static void ValidateReferenceImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Referans gorsel bulunamadi.", path);
        }

        if (!SupportedImages.Contains(Path.GetExtension(path)))
        {
            throw new InvalidOperationException("Referans gorsel formati desteklenmiyor.");
        }
    }

    private static void ValidateResult(VideoPromptCompositionResult result)
    {
        if (string.IsNullOrWhiteSpace(result.VideoPrompt) || result.VideoPrompt.Length < 20)
        {
            throw new InvalidOperationException("Qwen video promptu bos veya gecersiz dondurdu.");
        }

        if (result.VideoPrompt.Any(ch => ch > 255))
        {
            throw new InvalidOperationException("Qwen video promptu Ingilizce olmayan karakterler iceriyor.");
        }

        result.VideoPrompt = Limit(result.VideoPrompt, 1600);
        result.VideoNegativePrompt = Limit(result.VideoNegativePrompt, 900);
    }

    private static string BuildSystemPrompt()
    {
        return """
You are a cinematic image-to-video prompt director.
Analyze the supplied reference image and the supplied story context.
The reference image already defines character appearance, clothing, environment, composition, lighting and art style.
Do not redesign or extensively redescribe static details.
Write a concise English prompt for a short, single-shot image-to-video generation.
Focus on subject motion, facial expression changes, body movement, environmental motion, one coherent camera movement, how the scene evolves from the first frame to the final frame, physical plausibility, and preservation of character identity and scene continuity.
The shot must not contain cuts or abrupt angle changes.
Return only valid JSON matching the supplied schema.
""";
    }

    private static string BuildUserPrompt(VideoPromptCompositionRequest request)
    {
        return $"""
Create an English image-to-video prompt for scene {request.SceneNumber}.
Project: {request.ProjectName}
Story title: {request.StoryTitle}
Genre/style: {request.StoryGenre}; {request.VisualStyle}; {request.VideoStyle}; {request.AspectRatio}; {request.ClipDurationSeconds}s
Synopsis: {request.Synopsis}
World: {request.WorldDescription}
Visual direction: {request.VisualDirection}
Scene title: {request.SceneTitle}
Story beat: {request.StoryBeat}
Scene description: {request.SceneDescription}
Existing scene video prompt: {request.ExistingVideoPrompt}
Continuity from previous scene: {request.ContinuityFromPreviousScene}
Previous scene: {request.PreviousSceneTitle}; {request.PreviousSceneStoryBeat}
Next scene: {request.NextSceneTitle}; {request.NextSceneStoryBeat}
Characters: {request.Characters}
Location/time: {request.LocationDescription}; {request.TimeOfDay}
Avoid speech, lip sync, cuts, subtitles, text, watermarks and identity changes unless explicitly required by the scene.
""";
    }

    private static object BuildJsonSchema()
    {
        return new
        {
            type = "object",
            properties = new
            {
                videoPrompt = new { type = "string" },
                videoNegativePrompt = new { type = "string" },
                motionSummary = new { type = "string" },
                subjectActions = new { type = "array", items = new { type = "string" } },
                cameraMovement = new { type = "string" },
                environmentMotion = new { type = "array", items = new { type = "string" } },
                startState = new { type = "string" },
                endState = new { type = "string" },
                continuityPreserved = new { type = "array", items = new { type = "string" } },
                warnings = new { type = "array", items = new { type = "string" } }
            },
            required = new[] { "videoPrompt", "videoNegativePrompt", "motionSummary", "subjectActions", "cameraMovement", "environmentMotion", "startState", "endState", "continuityPreserved", "warnings" }
        };
    }

    private static string Limit(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
