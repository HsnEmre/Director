namespace Director.Services;

public static class FilmDurationPlanner
{
    public static int CalculateClipCountForMinutes(int totalDurationMinutes, int clipDurationSeconds) =>
        CalculateClipCountForTargetSeconds(totalDurationMinutes * 60, clipDurationSeconds);

    public static int CalculateClipCountForTargetSeconds(int targetDurationSeconds, int clipDurationSeconds)
    {
        if (targetDurationSeconds <= 0 || clipDurationSeconds <= 0)
        {
            return 0;
        }

        return (int)Math.Ceiling(targetDurationSeconds / (double)clipDurationSeconds);
    }

    public static int CalculateOutputDurationSeconds(int clipCount, int clipDurationSeconds) =>
        Math.Max(0, clipCount) * Math.Max(0, clipDurationSeconds);
}
