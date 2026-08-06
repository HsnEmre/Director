using System.IO;
using System.Windows.Media.Imaging;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class MediaFileService : IMediaFileService
{
    private static readonly HashSet<string> AllowedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };
    private static readonly HashSet<string> AllowedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv"
    };
    private static readonly HashSet<string> AllowedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".flac", ".mp3", ".ogg", ".m4a"
    };

    private readonly WanGpOptions _options;
    private readonly IImageThumbnailService _thumbnailService;
    private readonly IVideoMetadataService _metadataService;
    private readonly ILogger<MediaFileService> _logger;

    public MediaFileService(
        IOptions<WanGpOptions> options,
        IImageThumbnailService thumbnailService,
        IVideoMetadataService metadataService,
        ILogger<MediaFileService> logger)
    {
        _options = options.Value;
        _thumbnailService = thumbnailService;
        _metadataService = metadataService;
        _logger = logger;
    }

    public Task<SceneMediaAsset> CopyGeneratedImageAsync(
        FilmScene scene,
        GenerationJob job,
        WanGpJobSnapshot snapshot,
        int versionNumber,
        bool isSelected,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot.OutputPath))
        {
            throw new FileNotFoundException("WanGP output dosyasi bulunamadi.", snapshot.OutputPath);
        }

        return CopyImageAsync(scene, job, snapshot.OutputPath, versionNumber, isSelected, snapshot.Seed, cancellationToken);
    }

    public async Task<SceneMediaAsset> CopyImageAsync(
        FilmScene scene,
        GenerationJob job,
        string sourcePath,
        int versionNumber,
        bool isSelected,
        int? seed = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("WanGP output dosyasi bulunamadi.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (!AllowedImageExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"Desteklenmeyen gorsel uzantisi: {extension}");
        }

        var (width, height) = ReadImageSize(sourcePath);
        var root = Path.GetFullPath(_options.GetEffectiveOutputRootPath());
        var targetDirectory = Path.Combine(root, scene.FilmProjectId.ToString(), "scenes", scene.SceneNumber.ToString("000"), "images");
        Directory.CreateDirectory(targetDirectory);

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
        if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gecersiz hedef dosya yolu.");
        }

        await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Create(targetPath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        var fileInfo = new FileInfo(targetPath);
        string? thumbnailPath = null;
        try
        {
            thumbnailPath = await _thumbnailService.CreateThumbnailAsync(targetPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail uretilemedi. Ana gorsel korunacak.");
        }

        return new SceneMediaAsset
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            GenerationJobId = job.Id,
            MediaType = MediaType.Image,
            FilePath = targetPath,
            ThumbnailPath = thumbnailPath,
            OriginalFileName = Path.GetFileName(sourcePath),
            FileExtension = extension,
            FileSize = fileInfo.Length,
            Width = width,
            Height = height,
            Seed = seed,
            ModelType = job.ModelType,
            MetadataJson = "{}",
            VersionNumber = versionNumber,
            IsSelected = isSelected,
            CreatedAt = DateTime.Now
        };
    }

    public async Task<SceneMediaAsset> CopyGeneratedAudioAsync(
        FilmScene scene,
        GenerationJob job,
        string sourcePath,
        VideoMetadata metadata,
        int versionNumber,
        MediaAssetRole role,
        string metadataJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("WanGP audio output dosyasi bulunamadi.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (!AllowedAudioExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"Desteklenmeyen audio uzantisi: {extension}");
        }

        var root = Path.GetFullPath(_options.GetEffectiveOutputRootPath());
        var relativeFolder = role is MediaAssetRole.SceneSpeechTrack or MediaAssetRole.SceneAudioMix
            ? Path.Combine("speech", "mix")
            : Path.Combine("speech", "segments");
        var targetDirectory = Path.Combine(root, scene.FilmProjectId.ToString(), "scenes", scene.SceneNumber.ToString("000"), relativeFolder);
        Directory.CreateDirectory(targetDirectory);

        var (sortOrder, speakerKey) = ReadSpeechNamingParts(metadataJson);
        var fileName = role is MediaAssetRole.SceneSpeechTrack or MediaAssetRole.SceneAudioMix
            ? $"scene_{scene.SceneNumber:000}_speech{extension.ToLowerInvariant()}"
            : $"{sortOrder:000}_{SanitizeFilenamePart(speakerKey)}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
        if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gecersiz hedef audio yolu.");
        }

        await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var target = File.Create(targetPath))
        {
            await source.CopyToAsync(target, cancellationToken);
        }

        var fileInfo = new FileInfo(targetPath);
        return new SceneMediaAsset
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            GenerationJobId = job.Id,
            MediaType = MediaType.Audio,
            Role = role,
            FilePath = targetPath,
            OriginalFileName = Path.GetFileName(sourcePath),
            FileExtension = extension,
            FileSize = fileInfo.Length,
            DurationSeconds = metadata.DurationSeconds,
            ModelType = job.ModelType,
            MetadataJson = metadataJson,
            VersionNumber = versionNumber,
            IsSelected = role is MediaAssetRole.SceneSpeechTrack or MediaAssetRole.SceneAudioMix,
            CreatedAt = DateTime.Now
        };
    }

    private static (int SortOrder, string SpeakerKey) ReadSpeechNamingParts(string metadataJson)
    {
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(metadataJson) as System.Text.Json.Nodes.JsonObject;
            var sortOrder = int.TryParse(json?["sortOrder"]?.ToString(), out var parsed) ? parsed : 0;
            var speakerKey = json?["speakerKey"]?.ToString() ?? "speaker";
            return (sortOrder, speakerKey);
        }
        catch
        {
            return (0, "speaker");
        }
    }

    private static string SanitizeFilenamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "speaker" : cleaned;
    }

    public async Task<SceneMediaAsset> CopyGeneratedVideoAsync(
        FilmScene scene,
        GenerationJob job,
        string sourcePath,
        VideoMetadata metadata,
        int versionNumber,
        bool isSelected,
        int sourceImageAssetId,
        string? fallbackThumbnailPath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            throw new FileNotFoundException("WanGP video output dosyasi bulunamadi.", sourcePath);
        }

        var extension = Path.GetExtension(sourcePath);
        if (!AllowedVideoExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"Desteklenmeyen video uzantisi: {extension}");
        }

        var root = Path.GetFullPath(_options.GetEffectiveOutputRootPath());
        var targetDirectory = Path.Combine(root, scene.FilmProjectId.ToString(), "scenes", scene.SceneNumber.ToString("000"), "videos");
        Directory.CreateDirectory(targetDirectory);

        var fileName = $"scene-{scene.SceneNumber:000}-video-v{versionNumber:000}-{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
        if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gecersiz hedef video yolu.");
        }

        var stagingPath = Path.GetFullPath(Path.Combine(targetDirectory, $"{fileName}.{Guid.NewGuid():N}.tmp"));
        try
        {
            await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var target = File.Create(stagingPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            var stagingInfo = new FileInfo(stagingPath);
            var sourceInfo = new FileInfo(sourcePath);
            if (stagingInfo.Length <= 0 || stagingInfo.Length != sourceInfo.Length)
            {
                throw new IOException("Director video staging kopyasi source boyutuyla uyusmuyor.");
            }

            var stagingMetadata = await _metadataService.ProbeAsync(stagingPath, cancellationToken);
            if (!stagingMetadata.HasVideo || stagingMetadata.DurationSeconds is null or <= 0)
            {
                throw new InvalidOperationException("Director video staging dosyasi ffprobe dogrulamasindan gecmedi.");
            }

            File.Move(stagingPath, targetPath);
        }
        catch
        {
            TryDeleteStaging(stagingPath);
            throw;
        }

        var fileInfo = new FileInfo(targetPath);
        return new SceneMediaAsset
        {
            FilmProjectId = scene.FilmProjectId,
            SceneId = scene.Id,
            GenerationJobId = job.Id,
            SourceMediaAssetId = sourceImageAssetId,
            MediaType = MediaType.Video,
            Role = MediaAssetRole.GeneratedSilentVideo,
            FilePath = targetPath,
            ThumbnailPath = fallbackThumbnailPath,
            OriginalFileName = Path.GetFileName(sourcePath),
            FileExtension = extension,
            FileSize = fileInfo.Length,
            Width = metadata.Width,
            Height = metadata.Height,
            DurationSeconds = metadata.DurationSeconds,
            Fps = metadata.Fps,
            FrameCount = metadata.FrameCount,
            Seed = job.SettingsJson.Contains("seed", StringComparison.OrdinalIgnoreCase) ? null : null,
            ModelType = job.ModelType,
            MetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata),
            VersionNumber = versionNumber,
            IsSelected = isSelected,
            CreatedAt = DateTime.Now
        };
    }

    private static void TryDeleteStaging(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch
        {
            // Best-effort cleanup; source output and DB state are handled by caller.
        }
    }

    private static (int Width, int Height) ReadImageSize(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidOperationException("Output dosyasi acilabilir bir gorsel degil.");
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
