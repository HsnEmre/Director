namespace Director.WanGp;

public sealed class VideoMetadata
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? DurationSeconds { get; set; }
    public double? Fps { get; set; }
    public int? FrameCount { get; set; }
    public string Codec { get; set; } = string.Empty;
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public string AudioCodec { get; set; } = string.Empty;
    public double? AudioDurationSeconds { get; set; }
    public int? AudioChannels { get; set; }
    public int? AudioSampleRate { get; set; }
    public string AudioChannelLayout { get; set; } = string.Empty;
    public string PixelFormat { get; set; } = string.Empty;
}
