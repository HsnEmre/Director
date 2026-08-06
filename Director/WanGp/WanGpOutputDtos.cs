namespace Director.WanGp;

public sealed class WanGpOutputSnapshot
{
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public Dictionary<string, WanGpOutputFileState> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WanGpOutputFileState
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
}

public sealed class WanGpOutputCandidate
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastWriteTime { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? Fps { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public bool IsTransient { get; set; }
    public int EvidenceScore { get; set; }
    public List<string> Evidence { get; set; } = [];
    public string DisplayName => System.IO.Path.GetFileName(FilePath);
    public string SizeText => $"{FileSize / 1024d / 1024d:0.00} MB";
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "Unknown";
}

public sealed class WanGpOutputCandidateDiagnostic
{
    public string FileName { get; set; } = string.Empty;
    public bool PathUnderRoot { get; set; }
    public bool IsTransient { get; set; }
    public bool Exists { get; set; }
    public long? Size { get; set; }
    public bool Stable { get; set; }
    public bool FfprobeValid { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public bool DurationValid { get; set; }
    public bool SnapshotNew { get; set; }
    public bool CreatedAfterStart { get; set; }
    public bool ModifiedInWindow { get; set; }
    public bool SeedMatch { get; set; }
    public bool ExternalJobMatch { get; set; }
    public bool FilenameStemMatch { get; set; }
    public string RejectionReason { get; set; } = string.Empty;
}

public sealed class WanGpOutputResolveResult
{
    public bool Success { get; set; }
    public bool IsAmbiguous { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<WanGpOutputCandidate> Candidates { get; set; } = [];
}

public enum WanGpOutputMediaKind
{
    Image,
    Video,
    Audio
}

public sealed class WanGpFinalOutputResolveRequest
{
    public WanGpOutputMediaKind MediaKind { get; set; }
    public WanGpOutputSnapshot BeforeSnapshot { get; set; } = new();
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public IReadOnlyList<string> ExplicitPaths { get; set; } = [];
    public string? ExternalJobId { get; set; }
    public int? JobId { get; set; }
    public int? SceneId { get; set; }
    public int? Seed { get; set; }
    public bool RequireAudio { get; set; }
    public TimeSpan? MaxWait { get; set; }
}

public sealed class WanGpFinalOutputResolution
{
    public WanGpOutputCandidate Candidate { get; set; } = new();
    public IReadOnlyList<WanGpOutputCandidate> Candidates { get; set; } = [];
    public IReadOnlyList<WanGpOutputCandidate> RejectedTransientCandidates { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public abstract class WanGpOutputResolutionException : Exception
{
    protected WanGpOutputResolutionException(string message) : base(message)
    {
    }

    public int? JobId { get; init; }
    public string? ExternalJobId { get; init; }
    public int? SceneId { get; init; }
    public int? Seed { get; init; }
    public string OutputRoot { get; init; } = string.Empty;
    public string? TransientCandidatePath { get; init; }
    public IReadOnlyList<WanGpOutputCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<WanGpOutputCandidateDiagnostic> CandidateDiagnostics { get; init; } = [];
    public long? LastObservedSize { get; init; }
    public DateTime? LastObservedWriteTime { get; init; }
}

public sealed class WanGpOutputFinalizationTimeoutException : WanGpOutputResolutionException
{
    public WanGpOutputFinalizationTimeoutException(string message) : base(message)
    {
    }

    public TimeSpan Timeout { get; init; }
}

public sealed class WanGpAmbiguousOutputException : WanGpOutputResolutionException
{
    public WanGpAmbiguousOutputException(string message) : base(message)
    {
    }
}
