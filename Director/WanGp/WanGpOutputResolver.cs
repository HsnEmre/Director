using System.IO;
using System.Windows.Media.Imaging;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.WanGp;

public sealed class WanGpOutputResolver : IWanGpOutputResolver
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private readonly WanGpOptions _options;

    public WanGpOutputResolver(IOptions<WanGpOptions> options)
    {
        _options = options.Value;
    }

    public WanGpOutputSnapshot CaptureSnapshot()
    {
        var snapshot = new WanGpOutputSnapshot();
        foreach (var path in EnumerateImageFiles())
        {
            var info = new FileInfo(path);
            snapshot.Files[path] = new WanGpOutputFileState
            {
                Path = path,
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc
            };
        }

        return snapshot;
    }

    public async Task<WanGpOutputResolveResult> ResolveImageOutputsAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IReadOnlyList<string> explicitPaths,
        TimeSpan? maxWait = null,
        CancellationToken cancellationToken = default)
    {
        var explicitCandidates = new List<WanGpOutputCandidate>();
        foreach (var path in explicitPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = await TryBuildCandidateAsync(path, requireUnderOutputRoot: false, cancellationToken);
            if (candidate is not null)
            {
                explicitCandidates.Add(candidate);
            }
        }

        if (explicitCandidates.Count > 0)
        {
            return new WanGpOutputResolveResult
            {
                Success = true,
                Message = "MCP generated_files/output_path ile output bulundu.",
                Candidates = explicitCandidates
            };
        }

        using var watcher = CreateWatcher();
        var deadline = DateTime.Now.Add(maxWait ?? TimeSpan.FromSeconds(20));
        List<WanGpOutputCandidate> candidates = [];
        while (DateTime.Now <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidates = await FindNewStableCandidatesAsync(beforeSnapshot, startedAt, cancellationToken);
            if (candidates.Count > 0)
            {
                break;
            }

            await Task.Delay(750, cancellationToken);
        }

        if (candidates.Count == 1)
        {
            return new WanGpOutputResolveResult
            {
                Success = true,
                Message = "Output klasoru snapshot farki ile output bulundu.",
                Candidates = candidates
            };
        }

        if (candidates.Count > 1)
        {
            return new WanGpOutputResolveResult
            {
                Success = false,
                IsAmbiguous = true,
                Message = "Birden fazla belirsiz WanGP output bulundu; otomatik baglama yapilmadi.",
                Candidates = candidates
            };
        }

        return new WanGpOutputResolveResult
        {
            Success = false,
            Message = "WanGP output dosyasi bulunamadi."
        };
    }

    public async Task<IReadOnlyList<WanGpOutputCandidate>> ScanExistingImageOutputsAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new List<WanGpOutputCandidate>();
        foreach (var path in EnumerateImageFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = await TryBuildCandidateAsync(path, requireUnderOutputRoot: true, cancellationToken);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.LastWriteTime)
            .Take(200)
            .ToList();
    }

    private async Task<List<WanGpOutputCandidate>> FindNewStableCandidatesAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        CancellationToken cancellationToken)
    {
        var candidates = new List<WanGpOutputCandidate>();
        foreach (var path in EnumerateImageFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            if (beforeSnapshot.Files.ContainsKey(fullPath))
            {
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length <= 0 || info.LastWriteTime < startedAt.AddSeconds(-2))
            {
                continue;
            }

            var candidate = await TryBuildCandidateAsync(fullPath, requireUnderOutputRoot: true, cancellationToken);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates.OrderBy(candidate => candidate.LastWriteTime).ToList();
    }

    private async Task<WanGpOutputCandidate?> TryBuildCandidateAsync(
        string path,
        bool requireUnderOutputRoot,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (requireUnderOutputRoot && !IsUnderOutputRoot(fullPath))
        {
            return null;
        }

        if (!File.Exists(fullPath) || !ImageExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return null;
        }

        var first = new FileInfo(fullPath);
        if (first.Length <= 0)
        {
            return null;
        }

        await Task.Delay(500, cancellationToken);
        var second = new FileInfo(fullPath);
        if (first.Length != second.Length || first.LastWriteTimeUtc != second.LastWriteTimeUtc)
        {
            return null;
        }

        try
        {
            using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                return null;
            }

            return new WanGpOutputCandidate
            {
                FilePath = fullPath,
                FileSize = second.Length,
                CreatedAt = second.CreationTime,
                LastWriteTime = second.LastWriteTime,
                Width = frame.PixelWidth,
                Height = frame.PixelHeight
            };
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<string> EnumerateImageFiles()
    {
        var outputRoot = _options.GetEffectiveOutputDirectory();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(outputRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFullPath);
    }

    private FileSystemWatcher? CreateWatcher()
    {
        var outputRoot = _options.GetEffectiveOutputDirectory();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return null;
        }

        var watcher = new FileSystemWatcher(outputRoot)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };
        return watcher;
    }

    private bool IsUnderOutputRoot(string fullPath)
    {
        var outputRoot = Path.GetFullPath(_options.GetEffectiveOutputDirectory());
        return fullPath.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, outputRoot, StringComparison.OrdinalIgnoreCase);
    }
}
