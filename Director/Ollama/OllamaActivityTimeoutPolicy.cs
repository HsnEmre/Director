namespace Director.Ollama;

public static class OllamaActivityTimeoutPolicy
{
    public static bool HasTimedOut(
        DateTimeOffset now,
        DateTimeOffset lastActivity,
        TimeSpan noActivityTimeout) =>
        now - lastActivity >= noActivityTimeout;
}
