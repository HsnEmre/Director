namespace Director.WanGp;

public sealed class VideoMetadata
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? Fps { get; set; }
    public int? FrameCount { get; set; }
    public string Codec { get; set; } = string.Empty;
    public bool HasAudio { get; set; }
}
