using System.Diagnostics;

namespace Director.Tests;

public sealed class MediaOutputRecoveryCliTests
{
    [Fact]
    public async Task NoArgs_ReturnsDryRunNoWrite()
    {
        var result = await RunToolAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"Mode\": \"DryRun\"", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"DbWriteCount\": 0", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"FileCopyCount\": 0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteWithoutJobId_ReturnsExit2NoWrite()
    {
        var result = await RunToolAsync("--write");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--job-id", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"DbWriteCount\": 0", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--job-id 0")]
    [InlineData("--job-id -1")]
    [InlineData("--job-id abc")]
    public async Task InvalidJobId_ReturnsExit2(string arguments)
    {
        var result = await RunToolAsync(arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Invalid --job-id", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownArgument_ReturnsExit2()
    {
        var result = await RunToolAsync("--banana");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown argument", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PositionalUnknownArgument_ReturnsExit2()
    {
        var result = await RunToolAsync("banana");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown argument", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteWithInvalidJobId_ReturnsExit2NoWrite()
    {
        var result = await RunToolAsync("--write --job-id 0");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Invalid --job-id", result.Output, StringComparison.Ordinal);
        Assert.Contains("\"FileCopyCount\": 0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationFailures_DoNotPrintStackTrace()
    {
        var result = await RunToolAsync("--unknown");

        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("at Director.", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunToolAsync(string arguments = "")
    {
        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("Tools\\MediaOutputRecovery\\MediaOutputRecovery.csproj");
        startInfo.ArgumentList.Add("--");
        foreach (var argument in SplitArguments(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("MediaOutputRecovery process could not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var output = await outputTask;
        var error = await errorTask;
        return (process.ExitCode, output + error);
    }

    private static IEnumerable<string> SplitArguments(string arguments) =>
        arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string FindRepoRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Director.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repo root not found.");
    }
}
