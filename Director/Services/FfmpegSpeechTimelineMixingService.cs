using System.Diagnostics;
using System.Globalization;
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

public sealed class FfmpegSpeechTimelineMixingService : ISpeechTimelineMixingService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IVideoMetadataService _metadataService;
    private readonly IMediaFileService _mediaFileService;
    private readonly IApplicationActivityCenter _activityCenter;
    private readonly WanGpOptions _options;

    public FfmpegSpeechTimelineMixingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IVideoMetadataService metadataService,
        IMediaFileService mediaFileService,
        IApplicationActivityCenter activityCenter,
        IOptions<WanGpOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _metadataService = metadataService;
        _mediaFileService = mediaFileService;
        _activityCenter = activityCenter;
        _options = options.Value;
    }

    public async Task<SceneMediaAsset> CreateSpeechTrackAsync(int sceneSpeechPlanId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.SceneSpeechPlans
            .Include(item => item.Scene)
            .Include(item => item.Segments)
            .FirstAsync(item => item.Id == sceneSpeechPlanId, cancellationToken);
        if (plan.Segments.Count == 0)
        {
            throw new InvalidOperationException("Bu sahnede konusma bulunmuyor.");
        }

        var targetDuration = plan.TargetDurationSeconds;
        var segments = plan.Segments.OrderBy(item => item.SortOrder).ToList();
        foreach (var segment in segments)
        {
            if (segment.ActualDurationSeconds is double actual && segment.StartTimeSeconds + actual > targetDuration + 0.05)
            {
                throw new InvalidOperationException("Replik konusma kanali suresini asiyor; metin kisaltilmali.");
            }
        }

        for (var i = 1; i < segments.Count; i++)
        {
            var previous = segments[i - 1];
            var current = segments[i];
            var previousEnd = previous.StartTimeSeconds + (previous.ActualDurationSeconds ?? previous.TargetDurationSeconds);
            if (current.StartTimeSeconds < previousEnd - 0.02)
            {
                throw new InvalidOperationException("Replik zamanlamalarinda cakisma var; konusma kanali olusturulamaz.");
            }
        }

        var segmentIds = segments.Select(item => item.Id).ToHashSet();
        var audioAssets = await db.SceneMediaAssets
            .AsNoTracking()
            .Where(item => item.SceneId == plan.SceneId && item.MediaType == MediaType.Audio && item.Role == MediaAssetRole.SpeechSegment)
            .ToListAsync(cancellationToken);
        var assetBySegmentId = audioAssets
            .Select(asset => (Asset: asset, SegmentId: ReadMetadataInt(asset.MetadataJson, "speechSegmentId")))
            .Where(item => item.SegmentId is int segmentId && segmentIds.Contains(segmentId) && File.Exists(item.Asset.FilePath))
            .GroupBy(item => item.SegmentId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.Asset.CreatedAt).First().Asset);

        var missing = segments.Where(segment => !assetBySegmentId.ContainsKey(segment.Id)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Eksik replik WAV asset'i var. Ilk eksik segmentId={missing[0].Id}.");
        }

        var ffmpeg = FindFfmpeg()
            ?? throw new InvalidOperationException("ffmpeg.exe bulunamadi. WanGP RootPath, Python env veya PATH icinde ffmpeg gerekli.");
        var tempPath = Path.Combine(Path.GetTempPath(), $"director_speech_mix_{Guid.NewGuid():N}.wav");
        try
        {
            await RunFfmpegAsync(ffmpeg, tempPath, targetDuration, segments, assetBySegmentId, cancellationToken);
            var metadata = await _metadataService.ProbeAsync(tempPath, cancellationToken);
            var actualDuration = metadata.DurationSeconds ?? 0;
            if (actualDuration < 9.95 || actualDuration > 10.05)
            {
                throw new InvalidOperationException($"Konusma kanali suresi 10 sn degil: {actualDuration:0.000} sn.");
            }

            var job = new GenerationJob
            {
                FilmProjectId = plan.FilmProjectId,
                SceneId = plan.SceneId,
                MediaType = MediaType.Audio,
                Provider = GenerationProvider.WanGp,
                Status = GenerationJobStatus.Completed,
                ModelType = "ffmpeg-speech-track",
                Prompt = $"sceneSpeechPlanId={plan.Id}; segmentCount={segments.Count}",
                SettingsJson = JsonSerializer.Serialize(new
                {
                    plan.Id,
                    targetDurationSeconds = targetDuration,
                    segmentAssetIds = assetBySegmentId.Values.Select(item => item.Id).ToArray()
                }),
                CurrentPhase = "Completed",
                CreatedAt = DateTime.Now,
                StartedAt = DateTime.Now,
                CompletedAt = DateTime.Now,
                ProgressPercentage = 100
            };
            db.GenerationJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            var existing = await db.SceneMediaAssets
                .Where(item => item.SceneId == plan.SceneId && item.MediaType == MediaType.Audio)
                .ToListAsync(cancellationToken);
            var versionNumber = existing.Count == 0 ? 1 : existing.Max(item => item.VersionNumber) + 1;
            var metadataJson = JsonSerializer.Serialize(new
            {
                sceneSpeechPlanId = plan.Id,
                segmentCount = segments.Count,
                targetDurationSeconds = targetDuration,
                duration = actualDuration,
                originalTimelinePreserved = true
            });
            var asset = await _mediaFileService.CopyGeneratedAudioAsync(
                plan.Scene,
                job,
                tempPath,
                metadata,
                versionNumber,
                MediaAssetRole.SceneSpeechTrack,
                metadataJson,
                cancellationToken);

            db.SceneMediaAssets.Add(asset);
            plan.Status = SpeechPlanStatus.Completed;
            plan.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
            _activityCenter.AddLog("SpeechTrackMixing", $"Konusma kanali olusturuldu: {asset.FilePath}", GenerationLogLevel.Success);
            return asset;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task RunFfmpegAsync(
        string ffmpeg,
        string outputPath,
        double targetDuration,
        IReadOnlyList<SceneSpeechSegment> segments,
        IReadOnlyDictionary<int, SceneMediaAsset> assetBySegmentId,
        CancellationToken cancellationToken)
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
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add(FormatSeconds(targetDuration));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("anullsrc=channel_layout=stereo:sample_rate=48000");
        foreach (var segment in segments)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(assetBySegmentId[segment.Id].FilePath);
        }

        var filters = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var delayMs = Math.Max(0, (int)Math.Round(segments[i].StartTimeSeconds * 1000));
            filters.Add($"[{i + 1}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay={delayMs}|{delayMs}[s{i}]");
        }

        var inputs = "[0:a]" + string.Concat(Enumerable.Range(0, segments.Count).Select(index => $"[s{index}]"));
        var fadeOutStart = Math.Max(0, targetDuration - 0.04);
        filters.Add($"{inputs}amix=inputs={segments.Count + 1}:duration=longest:normalize=0,apad,atrim=0:{FormatSeconds(targetDuration)},afade=t=in:st=0:d=0.02,afade=t=out:st={FormatSeconds(fadeOutStart)}:d=0.04[aout]");
        startInfo.ArgumentList.Add("-filter_complex");
        startInfo.ArgumentList.Add(string.Join(";", filters));
        startInfo.ArgumentList.Add("-map");
        startInfo.ArgumentList.Add("[aout]");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg baslatilamadi.");
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg konusma miks hatasi: {TrimProcessOutput(stderr)}");
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

    private static int? ReadMetadataInt(string metadataJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatSeconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string TrimProcessOutput(string value)
    {
        value = value.Trim();
        return value.Length <= 1200 ? value : value[^1200..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
