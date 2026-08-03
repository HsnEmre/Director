using System.Diagnostics;
using Director.Enums;
using Director.Services;

namespace Director.Tests;

public sealed class ApplicationActivityCenterTests
{
    [Fact]
    public void AddLog_TrimsVisibleLogCollectionToConfiguredLimit()
    {
        var center = new ApplicationActivityCenter();

        for (var index = 0; index < 1050; index++)
        {
            center.AddLog("Video", $"message-{index}", GenerationLogLevel.Information);
        }

        Assert.Equal(1000, center.Logs.Count);
        Assert.Equal("message-50", center.Logs[0].Message);
        Assert.Equal("message-1049", center.Logs[^1].Message);
    }

    [Fact]
    public void ClearLogs_RemovesVisibleLogs()
    {
        var center = new ApplicationActivityCenter();
        center.AddLog("Video", "ready", GenerationLogLevel.Success);

        center.ClearLogs();

        Assert.Empty(center.Logs);
    }

    [Fact]
    public void ChangedHandlerFailure_DoesNotBreakActivityCenter()
    {
        var center = new ApplicationActivityCenter();
        var observed = 0;
        center.Changed += (_, _) => throw new InvalidOperationException("subscriber failed");
        center.Changed += (_, _) => observed++;

        center.AddLog("Video", "ready", GenerationLogLevel.Success);

        Assert.Equal(1, observed);
        Assert.Single(center.Logs);
    }

    [Fact]
    public async Task AddLog_FromBackgroundThread_ReturnsPromptly()
    {
        var center = new ApplicationActivityCenter();
        var stopwatch = Stopwatch.StartNew();

        await Task.Run(() => center.AddLog("Video", "background", GenerationLogLevel.Information));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }
}
