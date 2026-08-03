using System.Diagnostics;
using System.IO;
using Director.Data;
using Director.Enums;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class FfmpegFinalMovieAssemblyService : IFinalMovieAssemblyService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IVideoMetadataService _metadataService;
    private readonly WanGpOptions _options;

    public FfmpegFinalMovieAssemblyService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IVideoMetadataService metadataService,
        IOptions<WanGpOptions> options)
    {
        _dbContextFactory = dbContextFactory;
        _metadataService = metadataService;
        _options = options.Value;
    }

    public async Task<string> AssembleLtxNativeDialogueMovieAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var clips = await db.SceneMediaAssets
            .AsNoTracking()
            .Include(item => item.Scene)
            .Where(item =>
                item.FilmProjectId == filmProjectId &&
                item.MediaType == MediaType.Video &&
                item.Role == MediaAssetRole.GeneratedNativeDialogueVideo)
            .OrderBy(item => item.Scene.SceneNumber)
            .ToListAsync(cancellationToken);
        if (clips.Count != 30)
        {
            throw new InvalidOperationException($"Final film icin 30 tamamlanmis native dialogue klibi gerekli. Mevcut={clips.Count}.");
        }

        foreach (var clip in clips)
        {
            if (!File.Exists(clip.FilePath))
            {
                throw new FileNotFoundException("Native dialogue klibi bulunamadi.", clip.FilePath);
            }

            var metadata = await _metadataService.ProbeAsync(clip.FilePath, cancellationToken);
            if (!metadata.HasAudio || metadata.DurationSeconds is < 9.5 or > 10.5)
            {
                throw new InvalidOperationException($"Klip final concat icin dogrulanamadi. SceneId={clip.SceneId}; HasAudio={metadata.HasAudio}; Duration={metadata.DurationSeconds:0.000}");
            }
        }

        var root = Path.GetFullPath(_options.GetEffectiveOutputRootPath());
        var outputDirectory = Path.Combine(root, filmProjectId.ToString(), "final");
        Directory.CreateDirectory(outputDirectory);
        var listPath = Path.Combine(outputDirectory, $"ltx_native_dialogue_concat_{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(outputDirectory, "ltx_native_dialogue_5min.mp4");
        await File.WriteAllLinesAsync(listPath, clips.Select(clip => $"file '{clip.FilePath.Replace("'", "'\\''")}'"), cancellationToken);

        try
        {
            var ffmpeg = FindFfmpeg()
                ?? throw new InvalidOperationException("ffmpeg.exe bulunamadi.");
            await RunFfmpegConcatAsync(ffmpeg, listPath, outputPath, cancellationToken);
            var finalMetadata = await _metadataService.ProbeAsync(outputPath, cancellationToken);
            if (!finalMetadata.HasAudio || finalMetadata.DurationSeconds is < 295 or > 305)
            {
                throw new InvalidOperationException($"Final film dogrulanamadi. HasAudio={finalMetadata.HasAudio}; Duration={finalMetadata.DurationSeconds:0.000}");
            }

            return outputPath;
        }
        finally
        {
            TryDelete(listPath);
        }
    }

    private static async Task RunFfmpegConcatAsync(string ffmpeg, string listPath, string outputPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("concat");
        startInfo.ArgumentList.Add("-safe");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(listPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffmpeg baslatilamadi.");
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(stderr.Trim());
        }
    }

    private string? FindFfmpeg()
    {
        if (!string.IsNullOrWhiteSpace(_options.RootPath) && Directory.Exists(_options.RootPath))
        {
            var found = Directory.EnumerateFiles(_options.RootPath, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null)
            {
                return found;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, "ffmpeg.exe"))
            .FirstOrDefault(File.Exists);
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
