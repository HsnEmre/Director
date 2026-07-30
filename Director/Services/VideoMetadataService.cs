using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class VideoMetadataService : IVideoMetadataService
{
    private readonly WanGpOptions _options;

    public VideoMetadataService(IOptions<WanGpOptions> options)
    {
        _options = options.Value;
    }

    public async Task<VideoMetadata> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException("Video dosyasi bulunamadi.", videoPath);
        }

        var ffprobe = FindFfprobe();
        if (ffprobe is null)
        {
            return new VideoMetadata { DurationSeconds = new FileInfo(videoPath).Length > 0 ? 1 : 0 };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add(videoPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("ffprobe baslatilamadi.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            return new VideoMetadata { DurationSeconds = new FileInfo(videoPath).Length > 0 ? 1 : 0 };
        }

        using var document = JsonDocument.Parse(stdout);
        var metadata = new VideoMetadata();
        if (document.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = ReadString(stream, "codec_type");
                if (codecType == "video")
                {
                    metadata.Width = ReadInt(stream, "width");
                    metadata.Height = ReadInt(stream, "height");
                    metadata.Codec = ReadString(stream, "codec_name");
                    metadata.FrameCount = ReadInt(stream, "nb_frames");
                    metadata.Fps = ParseRate(ReadString(stream, "avg_frame_rate"));
                    metadata.DurationSeconds = ReadDouble(stream, "duration");
                }
                else if (codecType == "audio")
                {
                    metadata.HasAudio = true;
                }
            }
        }

        if (document.RootElement.TryGetProperty("format", out var format))
        {
            metadata.DurationSeconds ??= ReadDouble(format, "duration");
        }

        return metadata;
    }

    private string? FindFfprobe()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.RootPath))
        {
            candidates.AddRange(Directory.EnumerateFiles(_options.RootPath, "ffprobe.exe", SearchOption.AllDirectories).Take(8));
        }

        if (!string.IsNullOrWhiteSpace(_options.PythonExecutablePath))
        {
            var envRoot = Directory.GetParent(_options.PythonExecutablePath)?.Parent?.FullName;
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot))
            {
                candidates.AddRange(Directory.EnumerateFiles(envRoot, "ffprobe.exe", SearchOption.AllDirectories).Take(8));
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var path in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(path, "ffprobe.exe");
            if (File.Exists(candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) ? value.ToString() : string.Empty;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        return int.TryParse(ReadString(element, property), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? ReadDouble(JsonElement element, string property)
    {
        return double.TryParse(ReadString(element, property), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static double? ParseRate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0/0")
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}
