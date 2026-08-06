using System.IO;
using System.Windows.Media.Imaging;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.WanGp;

public sealed class WanGpFinalOutputResolver : IWanGpFinalOutputResolver
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".flac", ".mp3", ".ogg", ".m4a"
    };

    private readonly WanGpOptions _options;
    private readonly IVideoMetadataService _metadataService;

    public WanGpFinalOutputResolver(IOptions<WanGpOptions> options, IVideoMetadataService metadataService)
    {
        _options = options.Value;
        _metadataService = metadataService;
    }

    public WanGpOutputSnapshot CaptureSnapshot(WanGpOutputMediaKind mediaKind)
    {
        var snapshot = new WanGpOutputSnapshot();
        foreach (var path in EnumerateMediaFiles(mediaKind))
        {
            var info = new FileInfo(path);
            snapshot.Files[Path.GetFullPath(path)] = new WanGpOutputFileState
            {
                Path = Path.GetFullPath(path),
                Length = info.Length,
                LastWriteTimeUtc = info.LastWriteTimeUtc
            };
        }

        return snapshot;
    }

    public async Task<WanGpFinalOutputResolution> ResolveAsync(
        WanGpFinalOutputResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        var timeout = request.MaxWait ?? TimeSpan.FromSeconds(30);
        var deadline = DateTime.UtcNow.Add(timeout);
        var transientCandidates = new List<WanGpOutputCandidate>();
        List<WanGpOutputCandidate> lastCandidates = [];
        List<WanGpOutputCandidateDiagnostic> lastDiagnostics = [];

        while (DateTime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<WanGpOutputCandidate>();
            var diagnostics = new List<WanGpOutputCandidateDiagnostic>();
            foreach (var path in BuildCandidatePaths(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = SafeFullPath(path);
                if (fullPath is null || !IsUnderOutputRoot(fullPath))
                {
                    diagnostics.Add(new WanGpOutputCandidateDiagnostic
                    {
                        FileName = string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFileName(path),
                        PathUnderRoot = false,
                        RejectionReason = fullPath is null ? "InvalidPath" : "PathOutsideOutputRoot"
                    });
                    continue;
                }

                if (IsTransientPath(fullPath))
                {
                    diagnostics.Add(new WanGpOutputCandidateDiagnostic
                    {
                        FileName = Path.GetFileName(fullPath),
                        PathUnderRoot = true,
                        IsTransient = true,
                        Exists = File.Exists(fullPath),
                        RejectionReason = "TransientPath"
                    });
                    var transient = TryBuildTransientCandidate(fullPath);
                    if (transient is not null)
                    {
                        transientCandidates.Add(transient);
                    }

                    continue;
                }

                if (!IsSupportedExtension(request.MediaKind, fullPath))
                {
                    diagnostics.Add(new WanGpOutputCandidateDiagnostic
                    {
                        FileName = Path.GetFileName(fullPath),
                        PathUnderRoot = true,
                        Exists = File.Exists(fullPath),
                        RejectionReason = "UnsupportedExtension"
                    });
                    continue;
                }

                var evaluation = await TryBuildFinalCandidateAsync(fullPath, request, cancellationToken);
                diagnostics.Add(evaluation.Diagnostic);
                if (evaluation.Candidate is not null)
                {
                    candidates.Add(evaluation.Candidate);
                }
            }

            lastDiagnostics = diagnostics;
            lastCandidates = candidates
                .GroupBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(candidate => candidate.EvidenceScore).First())
                .OrderByDescending(candidate => candidate.EvidenceScore)
                .ThenBy(candidate => candidate.LastWriteTime)
                .ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (lastCandidates.Count > 0)
            {
                var topScore = lastCandidates[0].EvidenceScore;
                var top = lastCandidates.Where(candidate => candidate.EvidenceScore == topScore).ToList();
                if (top.Count == 1)
                {
                    return new WanGpFinalOutputResolution
                    {
                        Candidate = top[0],
                        Candidates = lastCandidates,
                        RejectedTransientCandidates = Distinct(transientCandidates),
                        Message = "WanGP final output deterministic correlation ile cozumlendi."
                    };
                }

                throw BuildAmbiguousException(request, top, transientCandidates);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
        }

        throw BuildTimeoutException(request, timeout, lastCandidates, transientCandidates, lastDiagnostics);
    }

    public static bool IsTransientPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".download", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileNameWithoutExtension(name).EndsWith("_tmp", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> BuildCandidatePaths(WanGpFinalOutputResolveRequest request)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var explicitPath in request.ExplicitPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (seen.Add(explicitPath))
            {
                yield return explicitPath;
            }

            var fullPath = SafeFullPath(explicitPath);
            if (fullPath is not null)
            {
                foreach (var neighbor in EnumerateCorrelatedNeighbors(fullPath, request))
                {
                    if (seen.Add(neighbor))
                    {
                        yield return neighbor;
                    }
                }
            }
        }

        foreach (var path in EnumerateMediaFiles(request.MediaKind))
        {
            if (!seen.Add(path))
            {
                continue;
            }

            if (request.BeforeSnapshot.Files.ContainsKey(path))
            {
                continue;
            }

            yield return path;
        }
    }

    private IEnumerable<string> EnumerateCorrelatedNeighbors(string explicitFullPath, WanGpFinalOutputResolveRequest request)
    {
        var directory = Path.GetDirectoryName(explicitFullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        var stem = Path.GetFileNameWithoutExtension(explicitFullPath);
        if (stem.EndsWith("_tmp", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        var timestampPrefix = stem.Length >= 20 ? stem[..20] : stem;
        var seedText = request.Seed is int seed ? $"seed{seed}" : TryReadSeedFromPath(explicitFullPath) is int parsed ? $"seed{parsed}" : string.Empty;
        return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => IsSupportedExtension(request.MediaKind, path))
            .Where(path =>
                Path.GetFileNameWithoutExtension(path).StartsWith(timestampPrefix, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(seedText) && Path.GetFileName(path).Contains(seedText, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<WanGpCandidateEvaluation> TryBuildFinalCandidateAsync(
        string fullPath,
        WanGpFinalOutputResolveRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostic = new WanGpOutputCandidateDiagnostic
        {
            FileName = Path.GetFileName(fullPath),
            PathUnderRoot = true,
            SnapshotNew = !request.BeforeSnapshot.Files.ContainsKey(fullPath),
            SeedMatch = request.Seed is int seed && Path.GetFileName(fullPath).Contains($"seed{seed}", StringComparison.OrdinalIgnoreCase),
            ExternalJobMatch = !string.IsNullOrWhiteSpace(request.ExternalJobId) && Path.GetFileName(fullPath).Contains(request.ExternalJobId, StringComparison.OrdinalIgnoreCase),
            FilenameStemMatch = request.ExplicitPaths.Select(SafeFullPath).Where(path => path is not null).Select(path => path!).Any(explicitPath =>
            {
                var explicitStem = Path.GetFileNameWithoutExtension(explicitPath);
                if (explicitStem.EndsWith("_tmp", StringComparison.OrdinalIgnoreCase))
                {
                    explicitStem = explicitStem[..^4];
                }

                return Path.GetFileNameWithoutExtension(fullPath).StartsWith(explicitStem, StringComparison.OrdinalIgnoreCase);
            })
        };
        if (!File.Exists(fullPath))
        {
            diagnostic.Exists = false;
            diagnostic.RejectionReason = "Missing";
            return new WanGpCandidateEvaluation(null, diagnostic);
        }

        var first = new FileInfo(fullPath);
        diagnostic.Exists = true;
        diagnostic.Size = first.Length;
        diagnostic.CreatedAfterStart = first.LastWriteTime >= request.StartedAt.AddSeconds(-3);
        diagnostic.ModifiedInWindow = request.CompletedAt is DateTime completedAt &&
            Math.Abs((first.LastWriteTime - completedAt).TotalMinutes) <= 10;
        if (first.Length <= 0 || first.LastWriteTime < request.StartedAt.AddSeconds(-3))
        {
            diagnostic.RejectionReason = first.Length <= 0 ? "EmptyFile" : "BeforeJobStartWindow";
            return new WanGpCandidateEvaluation(null, diagnostic);
        }

        var last = first;
        for (var index = 0; index < 2; index++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            if (!File.Exists(fullPath))
            {
                diagnostic.RejectionReason = "DisappearedDuringStabilityCheck";
                return new WanGpCandidateEvaluation(null, diagnostic);
            }

            var next = new FileInfo(fullPath);
            if (next.Length != last.Length || next.LastWriteTimeUtc != last.LastWriteTimeUtc)
            {
                diagnostic.RejectionReason = "UnstableLastWriteOrSize";
                return new WanGpCandidateEvaluation(null, diagnostic);
            }

            last = next;
        }

        diagnostic.Stable = true;
        try
        {
            using (File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
            }
        }
        catch
        {
            diagnostic.RejectionReason = "ReadLockOrAccessDenied";
            return new WanGpCandidateEvaluation(null, diagnostic);
        }

        var candidate = new WanGpOutputCandidate
        {
            FilePath = fullPath,
            FileSize = last.Length,
            CreatedAt = last.CreationTime,
            LastWriteTime = last.LastWriteTime
        };

        if (request.MediaKind == WanGpOutputMediaKind.Image)
        {
            if (!TryReadImage(candidate))
            {
                diagnostic.RejectionReason = "ImageReadFailed";
                return new WanGpCandidateEvaluation(null, diagnostic);
            }
        }
        else
        {
            var metadata = await _metadataService.ProbeAsync(fullPath, cancellationToken);
            candidate.DurationSeconds = metadata.DurationSeconds;
            candidate.Width = metadata.Width ?? 0;
            candidate.Height = metadata.Height ?? 0;
            candidate.Fps = metadata.Fps;
            candidate.HasVideo = metadata.HasVideo;
            candidate.HasAudio = metadata.HasAudio;
            diagnostic.FfprobeValid = metadata.HasVideo || metadata.HasAudio || metadata.DurationSeconds is > 0;
            diagnostic.HasVideo = metadata.HasVideo;
            diagnostic.HasAudio = metadata.HasAudio;
            diagnostic.DurationValid = metadata.DurationSeconds is > 0;

            if (request.MediaKind == WanGpOutputMediaKind.Video &&
                (!metadata.HasVideo || metadata.DurationSeconds is null or <= 0))
            {
                diagnostic.RejectionReason = !metadata.HasVideo ? "VideoStreamMissing" : "DurationInvalid";
                return new WanGpCandidateEvaluation(null, diagnostic);
            }

            if (request.MediaKind == WanGpOutputMediaKind.Audio &&
                ((metadata.HasVideo && !metadata.HasAudio) || metadata.DurationSeconds is null or <= 0))
            {
                diagnostic.RejectionReason = !metadata.HasAudio ? "AudioStreamMissing" : "DurationInvalid";
                return new WanGpCandidateEvaluation(null, diagnostic);
            }

            if (request.RequireAudio && !metadata.HasAudio)
            {
                diagnostic.RejectionReason = "AudioStreamMissingRequired";
                return new WanGpCandidateEvaluation(null, diagnostic);
            }
        }

        ScoreCandidate(candidate, request);
        diagnostic.RejectionReason = string.Empty;
        return new WanGpCandidateEvaluation(candidate, diagnostic);
    }

    private static bool TryReadImage(WanGpOutputCandidate candidate)
    {
        try
        {
            using var stream = File.Open(candidate.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault();
            if (frame is null || frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
            {
                return false;
            }

            candidate.Width = frame.PixelWidth;
            candidate.Height = frame.PixelHeight;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ScoreCandidate(WanGpOutputCandidate candidate, WanGpFinalOutputResolveRequest request)
    {
        var evidence = candidate.Evidence;
        if (request.ExplicitPaths.Any(path => string.Equals(SafeFullPath(path), candidate.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            candidate.EvidenceScore += 100;
            evidence.Add("ExplicitFinalPath");
        }

        var seed = request.Seed ?? TryReadSeedFromPath(string.Join(" ", request.ExplicitPaths));
        if (seed is int seedValue && Path.GetFileName(candidate.FilePath).Contains($"seed{seedValue}", StringComparison.OrdinalIgnoreCase))
        {
            candidate.EvidenceScore += 40;
            evidence.Add("SeedMatch");
        }

        if (!string.IsNullOrWhiteSpace(request.ExternalJobId) &&
            Path.GetFileName(candidate.FilePath).Contains(request.ExternalJobId, StringComparison.OrdinalIgnoreCase))
        {
            candidate.EvidenceScore += 50;
            evidence.Add("ExternalJobIdMatch");
        }

        if (candidate.LastWriteTime >= request.StartedAt.AddSeconds(-3))
        {
            candidate.EvidenceScore += 25;
            evidence.Add("AfterJobStart");
        }

        if (request.CompletedAt is DateTime completedAt &&
            Math.Abs((candidate.LastWriteTime - completedAt).TotalMinutes) <= 10)
        {
            candidate.EvidenceScore += 20;
            evidence.Add("CompletionWindow");
        }

        foreach (var explicitPath in request.ExplicitPaths.Select(SafeFullPath).Where(path => path is not null).Select(path => path!))
        {
            var explicitStem = Path.GetFileNameWithoutExtension(explicitPath);
            if (explicitStem.EndsWith("_tmp", StringComparison.OrdinalIgnoreCase))
            {
                explicitStem = explicitStem[..^4];
            }

            if (Path.GetFileNameWithoutExtension(candidate.FilePath).StartsWith(explicitStem, StringComparison.OrdinalIgnoreCase))
            {
                candidate.EvidenceScore += 60;
                evidence.Add("TransientStemMatch");
                break;
            }
        }
    }

    private WanGpOutputCandidate? TryBuildTransientCandidate(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            return null;
        }

        var info = new FileInfo(fullPath);
        return new WanGpOutputCandidate
        {
            FilePath = fullPath,
            FileSize = info.Length,
            CreatedAt = info.CreationTime,
            LastWriteTime = info.LastWriteTime,
            IsTransient = true
        };
    }

    private IEnumerable<string> EnumerateMediaFiles(WanGpOutputMediaKind mediaKind)
    {
        var outputRoot = _options.GetEffectiveOutputDirectory();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return [];
        }

        return Directory.EnumerateFiles(outputRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => IsSupportedExtension(mediaKind, path))
            .Select(Path.GetFullPath);
    }

    private static bool IsSupportedExtension(WanGpOutputMediaKind mediaKind, string path)
    {
        var extension = Path.GetExtension(path);
        return mediaKind switch
        {
            WanGpOutputMediaKind.Image => ImageExtensions.Contains(extension),
            WanGpOutputMediaKind.Video => VideoExtensions.Contains(extension),
            WanGpOutputMediaKind.Audio => AudioExtensions.Contains(extension),
            _ => false
        };
    }

    private bool IsUnderOutputRoot(string fullPath)
    {
        var outputRoot = Path.GetFullPath(_options.GetEffectiveOutputDirectory());
        return fullPath.StartsWith(outputRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullPath, outputRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static int? TryReadSeedFromPath(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, "seed(?<seed>\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["seed"].Value, out var seed) ? seed : null;
    }

    private WanGpOutputFinalizationTimeoutException BuildTimeoutException(
        WanGpFinalOutputResolveRequest request,
        TimeSpan timeout,
        IReadOnlyList<WanGpOutputCandidate> candidates,
        IReadOnlyList<WanGpOutputCandidate> transientCandidates,
        IReadOnlyList<WanGpOutputCandidateDiagnostic> candidateDiagnostics)
    {
        var last = candidates.Concat(transientCandidates).OrderByDescending(candidate => candidate.LastWriteTime).FirstOrDefault();
        return new WanGpOutputFinalizationTimeoutException("WanGP output finalization timeout.")
        {
            JobId = request.JobId,
            ExternalJobId = request.ExternalJobId,
            SceneId = request.SceneId,
            Seed = request.Seed ?? TryReadSeedFromPath(string.Join(" ", request.ExplicitPaths)),
            OutputRoot = Path.GetFullPath(_options.GetEffectiveOutputDirectory()),
            TransientCandidatePath = transientCandidates.LastOrDefault()?.FilePath,
            Candidates = candidates.Concat(Distinct(transientCandidates)).ToList(),
            CandidateDiagnostics = candidateDiagnostics.ToList(),
            Timeout = timeout,
            LastObservedSize = last?.FileSize,
            LastObservedWriteTime = last?.LastWriteTime
        };
    }

    private WanGpAmbiguousOutputException BuildAmbiguousException(
        WanGpFinalOutputResolveRequest request,
        IReadOnlyList<WanGpOutputCandidate> candidates,
        IReadOnlyList<WanGpOutputCandidate> transientCandidates)
    {
        return new WanGpAmbiguousOutputException("Birden fazla WanGP final output adayi ayni kanit gucune sahip.")
        {
            JobId = request.JobId,
            ExternalJobId = request.ExternalJobId,
            SceneId = request.SceneId,
            Seed = request.Seed ?? TryReadSeedFromPath(string.Join(" ", request.ExplicitPaths)),
            OutputRoot = Path.GetFullPath(_options.GetEffectiveOutputDirectory()),
            TransientCandidatePath = transientCandidates.LastOrDefault()?.FilePath,
            Candidates = candidates.Concat(Distinct(transientCandidates)).ToList()
        };
    }

    private sealed record WanGpCandidateEvaluation(WanGpOutputCandidate? Candidate, WanGpOutputCandidateDiagnostic Diagnostic);

    private static IReadOnlyList<WanGpOutputCandidate> Distinct(IEnumerable<WanGpOutputCandidate> candidates) =>
        candidates
            .GroupBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
}
