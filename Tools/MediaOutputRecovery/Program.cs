using System.Text.Json;
using Director.Data;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var parse = ParseArgs(args);
if (!parse.Success)
{
    WriteJson(new { Error = parse.Error, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 2;
}

var parsed = parse.Values;
if (args.Length == 0)
{
    WriteJson(new
    {
        Mode = "DryRun",
        Message = "No arguments supplied. Provide --job-id <id> for a recovery dry-run.",
        DbWriteCount = 0,
        FileCopyCount = 0,
        WanGpSubmitCount = 0,
        OllamaCallCount = 0
    });
    return 0;
}

var write = parsed.ContainsKey("write");
if (write && !parsed.ContainsKey("job-id"))
{
    WriteJson(new { Error = "--write icin --job-id <positive integer> zorunlu.", DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 2;
}

if (parsed.TryGetValue("job-id", out var jobIdText) && (!int.TryParse(jobIdText, out var parsedJobId) || parsedJobId <= 0))
{
    WriteJson(new { Error = "Invalid --job-id. Pozitif integer olmali.", DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 2;
}

var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
var configuration = new ConfigurationBuilder()
    .SetBasePath(Path.Combine(repoRoot, "Director"))
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));
services.Configure<WanGpOptions>(configuration.GetSection("WanGp"));
services.AddSingleton<IVideoMetadataService, VideoMetadataService>();
services.AddSingleton<IWanGpFinalOutputResolver, WanGpFinalOutputResolver>();
services.AddSingleton<IImageThumbnailService, ImageThumbnailService>();
services.AddSingleton<IMediaFileService, MediaFileService>();
services.AddSingleton<IMediaOutputRecoveryLeaseCoordinator, MediaOutputRecoveryLeaseCoordinator>();
services.AddSingleton<IMediaOutputRecoveryService, MediaOutputRecoveryService>();

await using var provider = services.BuildServiceProvider();
var service = provider.GetRequiredService<IMediaOutputRecoveryService>();
var request = new MediaOutputRecoveryRequest
{
    GenerationJobId = int.TryParse(jobIdText, out var jobId) ? jobId : null,
    FilmProjectId = ReadInt(parsed, "film-project-id"),
    SceneId = ReadInt(parsed, "scene-id"),
    Seed = ReadInt(parsed, "seed"),
    Write = write
};

try
{
    var plan = await service.PlanVideoRecoveryAsync(request);
    if (!write)
    {
        WritePlan(plan, "DryRun");
        return 0;
    }

    WritePlan(plan, "Write");
    if (plan.Ambiguous)
    {
        return 5;
    }

    if (!plan.RecoveryPossible)
    {
        return 4;
    }

    var result = await service.WriteVideoRecoveryAsync(request);
    WriteJson(result);
    return 0;
}
catch (MediaOutputRecoveryBusyException ex)
{
    WriteJson(new { Error = ex.Message, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 3;
}
catch (WanGpAmbiguousOutputException ex)
{
    WriteJson(new { Error = ex.Message, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 5;
}
catch (MediaOutputRecoveryNotPossibleException ex)
{
    WriteJson(new { Error = ex.Message, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 4;
}
catch (MediaOutputRecoveryImportException ex)
{
    WriteJson(new { Error = ex.Message, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 6;
}
catch (MediaOutputRecoveryDbException ex)
{
    WriteJson(new { Error = ex.Message, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 7;
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
{
    WriteJson(new { Error = ex.Message, DbWriteCount = 0, FileCopyCount = 0, WanGpSubmitCount = 0, OllamaCallCount = 0 });
    return 2;
}

static ParseResult ParseArgs(string[] args)
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "job-id", "film-project-id", "scene-id", "seed", "write"
    };
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < args.Length; index++)
    {
        var arg = args[index];
        if (!arg.StartsWith("--", StringComparison.Ordinal))
        {
            return new ParseResult(false, values, $"Unknown argument: {arg}");
        }

        var key = arg[2..];
        if (!allowed.Contains(key))
        {
            return new ParseResult(false, values, $"Unknown argument: --{key}");
        }

        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            values[key] = args[++index];
        }
        else
        {
            values[key] = "true";
        }
    }

    return new ParseResult(true, values, string.Empty);
}

static int? ReadInt(Dictionary<string, string> parsed, string key) =>
    parsed.TryGetValue(key, out var value) && int.TryParse(value, out var parsedValue) ? parsedValue : null;

static void WritePlan(MediaOutputRecoveryPlan plan, string mode)
{
    WriteJson(new
    {
        Mode = mode,
        plan.GenerationJobId,
        plan.FilmProjectId,
        plan.SceneId,
        plan.SceneNumber,
        SourceFinalFileName = Path.GetFileName(plan.ResolvedFinalPath),
        SourceSize = plan.FinalSize,
        Duration = plan.DurationSeconds,
        plan.HasVideo,
        plan.HasAudio,
        plan.IntendedDestination,
        plan.ExistingVideoAssetCount,
        RecoveryConfidenceEvidence = plan.Evidence,
        plan.RecoveryPossible,
        plan.Ambiguous,
        plan.Message,
        WanGpSubmitCount = 0,
        OllamaCallCount = 0,
        DbWriteCount = 0,
        FileCopyCount = 0
    });
}

static void WriteJson<T>(T value) =>
    Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

static string FindRepoRoot(string start)
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

    return Directory.GetCurrentDirectory();
}

internal sealed record ParseResult(bool Success, Dictionary<string, string> Values, string Error);
