using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Director.Data;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class FfmpegFinalDialogueVideoMuxingService : IFinalDialogueVideoMuxingService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IVideoMetadataService _metadataService;
    private readonly IApplicationActivityCenter _activityCenter;
    private readonly WanGpOptions _options;

    public FfmpegFinalDialogueVideoMuxingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IVideoMetadataService metadataService,
        IApplicationActivityCenter activityCenter,
        IOptions<WanGpOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _metadataService = metadataService;
        _activityCenter = activityCenter;
        _options = options.Value;
    }

    public async Task<SceneMediaAsset> CreateFinalDialogueVideoAsync(int videoAssetId, int speechTrackAssetId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var video = await db.SceneMediaAssets.Include(item => item.Scene).AsNoTracking().FirstAsync(item => item.Id == videoAssetId, cancellationToken);
        var speech = await db.SceneMediaAssets.AsNoTracking().FirstAsync(item => item.Id == speechTrackAssetId, cancellationToken);
        if (video.SceneId != speech.SceneId)
        {
            throw new InvalidOperationException("Video ve konusma kanali ayni sahneye ait degil.");
        }

        if (video.MediaType != MediaType.Video || !File.Exists(video.FilePath))
        {
            throw new InvalidOperationException("Gecerli kaynak video asset'i bulunamadi.");
        }

        if (speech.MediaType != MediaType.Audio || speech.Role != MediaAssetRole.SceneSpeechTrack || !File.Exists(speech.FilePath))
        {
            throw new InvalidOperationException("Gecerli konusma kanali asset'i bulunamadi.");
        }

        var ffmpeg = FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg.exe bulunamadi. WanGP RootPath, Python env veya PATH icinde ffmpeg gerekli.");
        var root = Path.GetFullPath(_options.GetEffectiveOutputRootPath());
        var outputDirectory = Path.Combine(root, video.FilmProjectId.ToString(), "scenes", video.Scene.SceneNumber.ToString("000"), "final");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.GetFullPath(Path.Combine(outputDirectory, $"scene_{video.Scene.SceneNumber:000}_dialogue.mp4"));
        if (!outputPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gecersiz final video hedef yolu.");
        }

        await RunFfmpegAsync(ffmpeg, video.FilePath, speech.FilePath, outputPath, cancellationToken);
        var metadata = await _metadataService.ProbeAsync(outputPath, cancellationToken);
        if (metadata.HasAudio != true)
        {
            throw new InvalidOperationException("Final dialogue MP4 icinde ses stream'i dogrulanamadi.");
        }

        var duration = metadata.DurationSeconds ?? 0;
        if (duration < 9.5 || duration > 10.5)
        {
            throw new InvalidOperationException($"Final dialogue MP4 suresi beklenen 10 sn araliginda degil: {duration:0.000} sn.");
        }

        var existing = await db.SceneMediaAssets
            .Where(item => item.SceneId == video.SceneId && item.MediaType == MediaType.Video)
            .ToListAsync(cancellationToken);
        var versionNumber = existing.Count == 0 ? 1 : existing.Max(item => item.VersionNumber) + 1;
        var job = new GenerationJob
        {
            FilmProjectId = video.FilmProjectId,
            SceneId = video.SceneId,
            SourceMediaAssetId = video.Id,
            MediaType = MediaType.Video,
            Provider = GenerationProvider.WanGp,
            Status = GenerationJobStatus.Completed,
            ModelType = "ffmpeg-dialogue-mux",
            Prompt = $"videoAssetId={video.Id}; speechTrackAssetId={speech.Id}",
            SettingsJson = JsonSerializer.Serialize(new
            {
                videoAssetId = video.Id,
                speechTrackAssetId = speech.Id,
                originalAudioRemoved = true,
                audioSource = "Director speech track"
            }),
            CurrentPhase = "Completed",
            CreatedAt = DateTime.Now,
            StartedAt = DateTime.Now,
            CompletedAt = DateTime.Now,
            ProgressPercentage = 100
        };
        db.GenerationJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var asset in await db.SceneMediaAssets.Where(item => item.SceneId == video.SceneId && item.MediaType == MediaType.Video).ToListAsync(cancellationToken))
        {
            asset.IsSelected = false;
        }

        var fileInfo = new FileInfo(outputPath);
        var finalAsset = new SceneMediaAsset
        {
            FilmProjectId = video.FilmProjectId,
            SceneId = video.SceneId,
            GenerationJobId = job.Id,
            SourceMediaAssetId = video.Id,
            MediaType = MediaType.Video,
            Role = MediaAssetRole.FinalDialogueVideo,
            FilePath = outputPath,
            ThumbnailPath = video.ThumbnailPath,
            OriginalFileName = Path.GetFileName(outputPath),
            FileExtension = ".mp4",
            FileSize = fileInfo.Length,
            Width = metadata.Width ?? video.Width,
            Height = metadata.Height ?? video.Height,
            DurationSeconds = metadata.DurationSeconds,
            Fps = metadata.Fps ?? video.Fps,
            FrameCount = metadata.FrameCount,
            ModelType = job.ModelType,
            MetadataJson = JsonSerializer.Serialize(new
            {
                videoAssetId = video.Id,
                speechTrackAssetId = speech.Id,
                originalAudioRemoved = true,
                duration = metadata.DurationSeconds,
                hasAudio = metadata.HasAudio
            }),
            VersionNumber = versionNumber,
            IsSelected = true,
            CreatedAt = DateTime.Now
        };
        db.SceneMediaAssets.Add(finalAsset);
        await db.SaveChangesAsync(cancellationToken);
        _activityCenter.AddLog("FinalDialogueMuxing", $"Final konusmali video olusturuldu: {finalAsset.FilePath}", GenerationLogLevel.Success);
        return finalAsset;
    }

    private static async Task RunFfmpegAsync(string ffmpeg, string videoPath, string speechPath, string outputPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(videoPath);
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(speechPath);
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("0:v:0");
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("1:a:0");
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("192k");
        startInfo.ArgumentList.Add("-shortest");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg baslatilamadi.");
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg mux hatasi: {TrimProcessOutput(stderr)}");
        }
    }

    private string? FindFfmpeg()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.RootPath) && Directory.Exists(_options.RootPath))
        {
            candidates.AddRange(Directory.EnumerateFiles(_options.RootPath, "ffmpeg.exe", SearchOption.AllDirectories).Take(8));
        }

        if (!string.IsNullOrWhiteSpace(_options.PythonExecutablePath))
        {
            var envRoot = Directory.GetParent(_options.PythonExecutablePath)?.Parent?.FullName;
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
            {
                candidates.AddRange(Directory.EnumerateFiles(envRoot, "ffmpeg.exe", SearchOption.AllDirectories).Take(8));
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(path, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string TrimProcessOutput(string value)
    {
        value = value.Trim();
        return value.Length <= 1200 ? value : value[^1200..];
    }
}
