namespace Director.WanGp;

public enum WanGpVideoDurationUnit
{
    Seconds,
    Frames,
    Milliseconds,
    EnumValue
}

public sealed class WanGpVideoTimingContract
{
    public string DurationKey { get; set; } = string.Empty;
    public WanGpVideoDurationUnit DurationUnit { get; set; } = WanGpVideoDurationUnit.Frames;
    public string FpsKey { get; set; } = string.Empty;
    public string FrameCountKey { get; set; } = string.Empty;
    public double DefaultFps { get; set; } = 24;
    public double SelectedFps { get; set; } = 24;
    public int RequestedDurationSeconds { get; set; }
    public int AppliedDurationSeconds { get; set; }
    public int CalculatedFrameCount { get; set; }
    public int FrameAlignment { get; set; } = 1;
    public int MinimumFrameCount { get; set; } = 1;
    public int MaximumFrameCount { get; set; } = 240;
    public List<string> Evidence { get; set; } = [];
    public bool IsValidated { get; set; }
}
