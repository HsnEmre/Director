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

    private readonly WanGpOptions _options;
    private readonly IImageThumbnailService _thumbnailService;
    private readonly ILogger<MediaFileService> _logger;

    public MediaFileService(
        IOptions<WanGpOptions> options,
        IImageThumbnailService thumbnailService,
        ILogger<MediaFileService> logger)
    {
        _options = options.Value;
        _thumbnailService = thumbnailService;
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

        var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var targetPath = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
        if (!targetPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gecersiz hedef video yolu.");
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
            SourceMediaAssetId = sourceImageAssetId,
            MediaType = MediaType.Video,
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

    private static (int Width, int Height) ReadImageSize(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.FirstOrDefault()
            ?? throw new InvalidOperationException("Output dosyasi acilabilir bir gorsel degil.");
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
