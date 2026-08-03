namespace Director.Services;

internal static class GenerationCleanupPolicy
{
    public const int CleanupTimeoutSeconds = 10;

    public static CancellationTokenSource CreateTokenSource() =>
        new(TimeSpan.FromSeconds(CleanupTimeoutSeconds));
}
