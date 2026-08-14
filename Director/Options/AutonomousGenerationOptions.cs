namespace Director.Options;

public sealed class AutonomousGenerationOptions
{
    public int PollIntervalSeconds { get; set; } = 5;
    public int StaleHeartbeatSeconds { get; set; } = 600;
    public int LeaseExtensionSeconds { get; set; } = 900;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int MediaRetryDelaySeconds { get; set; } = 5;

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));
    public TimeSpan StaleHeartbeatThreshold => TimeSpan.FromSeconds(Math.Max(1, StaleHeartbeatSeconds));
    public TimeSpan LeaseExtension => TimeSpan.FromSeconds(Math.Max(1, LeaseExtensionSeconds));
    public TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(Math.Max(1, HeartbeatIntervalSeconds));
    public TimeSpan MediaRetryDelay => TimeSpan.FromSeconds(Math.Max(0, MediaRetryDelaySeconds));
}
