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
    public string DisplayName => System.IO.Path.GetFileName(FilePath);
    public string SizeText => $"{FileSize / 1024d / 1024d:0.00} MB";
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}x{Height}" : "Unknown";
}

public sealed class WanGpOutputResolveResult
{
    public bool Success { get; set; }
    public bool IsAmbiguous { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<WanGpOutputCandidate> Candidates { get; set; } = [];
}
