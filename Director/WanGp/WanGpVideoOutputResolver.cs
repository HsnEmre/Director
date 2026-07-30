using System.IO;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.WanGp;

public sealed class WanGpVideoOutputResolver : IWanGpVideoOutputResolver
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv"
    };

    private readonly WanGpOptions _options;
    private readonly IVideoMetadataService _metadataService;

    public WanGpVideoOutputResolver(IOptions<WanGpOptions> options, IVideoMetadataService metadataService)
    {
        _options = options.Value;
        _metadataService = metadataService;
    }

    public WanGpOutputSnapshot CaptureSnapshot()
    {
        var snapshot = new WanGpOutputSnapshot();
        foreach (var path in EnumerateVideoFiles())
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

    public async Task<WanGpOutputResolveResult> ResolveVideoOutputsAsync(
        WanGpOutputSnapshot beforeSnapshot,
        DateTime startedAt,
        IReadOnlyList<string> explicitPaths,
        TimeSpan? maxWait = null,
        CancellationToken cancellationToken = default)
    {
        var explicitCandidates = new List<WanGpOutputCandidate>();
        foreach (var path in explicitPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = await TryBuildCandidateAsync(path, false, cancellationToken);
            if (candidate is not null)
            {
                explicitCandidates.Add(candidate);
            }
        }

        if (explicitCandidates.Count > 0)
        {
            return new WanGpOutputResolveResult { Success = true, Message = "MCP video output yolu ile output bulundu.", Candidates = explicitCandidates };
        }

        var deadline = DateTime.Now.Add(maxWait ?? TimeSpan.FromSeconds(30));
        while (DateTime.Now <= deadline)
        {
            var candidates = new List<WanGpOutputCandidate>();
            foreach (var path in EnumerateVideoFiles())
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

                var candidate = await TryBuildCandidateAsync(fullPath, true, cancellationToken);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count == 1)
            {
                return new WanGpOutputResolveResult { Success = true, Message = "Output klasoru snapshot farki ile video bulundu.", Candidates = candidates };
            }

            if (candidates.Count > 1)
            {
                return new WanGpOutputResolveResult { Success = false, IsAmbiguous = true, Message = "Birden fazla belirsiz video output bulundu.", Candidates = candidates };
            }

            await Task.Delay(1000, cancellationToken);
        }

        return new WanGpOutputResolveResult { Success = false, Message = "WanGP video output dosyasi bulunamadi." };
    }

    private async Task<WanGpOutputCandidate?> TryBuildCandidateAsync(string path, bool requireUnderOutputRoot, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (requireUnderOutputRoot && !IsUnderOutputRoot(fullPath))
        {
            return null;
        }

        if (!File.Exists(fullPath) || !VideoExtensions.Contains(Path.GetExtension(fullPath)))
        {
            return null;
        }

        var first = new FileInfo(fullPath);
        await Task.Delay(1000, cancellationToken);
        var second = new FileInfo(fullPath);
        if (first.Length <= 0 || first.Length != second.Length || first.LastWriteTimeUtc != second.LastWriteTimeUtc)
        {
            return null;
        }

        var metadata = await _metadataService.ProbeAsync(fullPath, cancellationToken);
        if (metadata.DurationSeconds is <= 0)
        {
            return null;
        }

        return new WanGpOutputCandidate
        {
            FilePath = fullPath,
            FileSize = second.Length,
            CreatedAt = second.CreationTime,
            LastWriteTime = second.LastWriteTime,
            Width = metadata.Width ?? 0,
            Height = metadata.Height ?? 0
        };
    }

    private IEnumerable<string> EnumerateVideoFiles()
    {
        var outputRoot = _options.GetEffectiveOutputDirectory();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(outputRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => VideoExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFullPath);
    }

    private bool IsUnderOutputRoot(string fullPath)
    {
        var outputRoot = Path.GetFullPath(_options.GetEffectiveOutputDirectory());
        return fullPath.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, outputRoot, StringComparison.OrdinalIgnoreCase);
    }
}
