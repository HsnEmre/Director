using System.Net.Http;
using Director.Data;
using Director.Dtos.StoryGeneration;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var parse = StoryGenerationSmokeArguments.Parse(args);
if (parse.ShowHelp)
{
    StoryGenerationSmokeArguments.WriteHelp(Console.Out);
    return 0;
}

if (!parse.Success)
{
    Console.Error.WriteLine(parse.ErrorMessage);
    StoryGenerationSmokeArguments.WriteHelp(Console.Error);
    return 2;
}

var smokeOptions = parse.Options;
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var configuration = new ConfigurationBuilder()
    .SetBasePath(Path.Combine(root, "Director"))
    .AddJsonFile("appsettings.json", optional: false)
    .Build();
var connectionString = configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection bulunamadi.");
var databaseLabel = SmokeDatabaseLabel.FromConnectionString(connectionString);

Console.WriteLine("StoryGenerationSmoke");
Console.WriteLine($"Mode={(smokeOptions.Write ? "Write" : "DryRun")}");
Console.WriteLine($"TargetProvider={databaseLabel.Provider}");
Console.WriteLine($"TargetDatabase={databaseLabel.DatabaseName}");
Console.WriteLine($"ProjectId={smokeOptions.ProjectId?.ToString() ?? "(not specified)"}");
Console.WriteLine($"MaxScenes={smokeOptions.MaxScenes}");

if (!smokeOptions.Write)
{
    if (smokeOptions.ProjectId is int readOnlyProjectId)
    {
        var readOnlySnapshot = await SmokeProjectSnapshot.LoadAsync(connectionString, readOnlyProjectId);
        readOnlySnapshot.WriteTo(Console.Out);
    }
    else
    {
        Console.WriteLine("Dry-run tamamlandi. DB write yok. Ollama cagrisi yok. Project analizi icin --project-id <id> verin.");
    }

    return 0;
}

var services = new ServiceCollection();
services.Configure<OllamaOptions>(configuration.GetSection("Ollama"));
services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(connectionString));
services.AddHttpClient<IOllamaClient, OllamaClient>()
    .ConfigurePrimaryHttpMessageHandler(serviceProvider => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(Math.Max(
            1,
            serviceProvider.GetRequiredService<IOptions<OllamaOptions>>().Value.SceneConnectTimeoutSeconds))
    });
services.AddSingleton<IStoryPromptBuilder, StoryPromptBuilder>();
services.AddSingleton<IOllamaFailureDiagnosticWriter, OllamaFailureDiagnosticWriter>();
services.AddSingleton<IGpuGenerationCoordinator, GpuGenerationCoordinator>();
services.AddSingleton<IProjectGenerationLeaseCoordinator, ProjectGenerationLeaseCoordinator>();
services.AddSingleton<IStoryGenerationService, StoryGenerationService>();

await using var provider = services.BuildServiceProvider();
var service = provider.GetRequiredService<IStoryGenerationService>();
var dbFactory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
var gpuCoordinator = provider.GetRequiredService<IGpuGenerationCoordinator>();
var ollamaOptions = provider.GetRequiredService<IOptions<OllamaOptions>>().Value;
var projectId = smokeOptions.ProjectId!.Value;

var beforeWriteSnapshot = await SmokeProjectSnapshot.LoadAsync(connectionString, projectId);
beforeWriteSnapshot.WriteTo(Console.Out);
Console.WriteLine("WriteGuard=enabled");
Console.WriteLine($"ScenesToGenerateLimit={smokeOptions.MaxScenes}");
Console.WriteLine($"StoryTextModel={ollamaOptions.StoryTextModel}");
Console.WriteLine($"SceneTextModel={ollamaOptions.SceneTextModel}");
Console.WriteLine($"PromptPreparationModel={ollamaOptions.PromptPreparationModel}");
var startedAt = DateTime.Now;
Console.WriteLine($"StartedAt={startedAt:O}");
var fourBCallCount = 0;
var ollamaCallCount = 0;
var responseCharacterCount = 0;
var responseTokenCount = 0;
var streamDone = false;
var doneReason = string.Empty;

var progress = new InlineProgress<StoryGenerationProgress>(item =>
{
    if (item.Message.Contains("Qwen 30B istegi gonderildi", StringComparison.OrdinalIgnoreCase))
    {
        ollamaCallCount++;
    }

    if (item.Message.Contains("istegi gonderildi", StringComparison.OrdinalIgnoreCase) &&
        item.Message.Contains("qwen3:4b", StringComparison.OrdinalIgnoreCase))
    {
        fourBCallCount++;
    }

    if (item.Message.StartsWith("Cevap tamamlandi.", StringComparison.OrdinalIgnoreCase))
    {
        responseCharacterCount = ReadMetric(item.Message, "Karakter");
        responseTokenCount = ReadMetric(item.Message, "ResponseToken");
        streamDone = item.Message.Contains("Done=True", StringComparison.OrdinalIgnoreCase);
        doneReason = ReadTextMetric(item.Message, "DoneReason");
    }

    Console.WriteLine($"{DateTime.Now:HH:mm:ss} | {item.Phase} | {item.CompletedItems}/{item.TotalItems} | {item.Percentage:0.0} | {item.Message}");
});

StoryGenerationProgressResult? result;
try
{
    result = await service.GenerateUpToMissingScenesAsync(projectId, smokeOptions.MaxScenes, progress);
}
catch (ProjectGenerationAlreadyRunningException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine($"ProjectBusy=True; ProjectId={ex.FilmProjectId}; DatabaseIdentity={ex.DatabaseIdentityShortHash}");
    return 3;
}

if (result is null)
{
    throw new InvalidOperationException("Write modu secildi ancak uretilecek sahne sayisi sifir.");
}

Console.WriteLine($"ResultStoryId={result.FilmStoryId}");
Console.WriteLine($"ResultSceneCount={result.GeneratedSceneCount}");

await using var db = await dbFactory.CreateDbContextAsync();
var story = await db.FilmStories.AsNoTracking().FirstAsync(item => item.FilmProjectId == projectId);
var characterCount = await db.StoryCharacters.AsNoTracking().CountAsync(item => item.FilmStoryId == story.Id);
var scenes = await db.FilmScenes.AsNoTracking()
    .Where(item => item.FilmProjectId == projectId)
    .OrderBy(item => item.SceneNumber)
    .Select(item => new { item.SceneNumber, item.DurationSeconds, item.ImagePrompt, item.VideoPrompt, item.DialogueJson })
    .ToListAsync();
var project = await db.FilmProjects.AsNoTracking().FirstAsync(item => item.Id == projectId);
var duplicateCount = scenes.GroupBy(item => item.SceneNumber).Count(group => group.Count() > 1);
var nextMissing = StoryGenerationService.FindFirstMissingScene(scenes.Select(item => item.SceneNumber).ToHashSet(), project.CalculatedClipCount);

Console.WriteLine($"SavedStoryId={story.Id}");
Console.WriteLine($"SavedCharacterCount={characterCount}");
Console.WriteLine($"SavedSceneCount={scenes.Count}");
Console.WriteLine($"TotalDurationSeconds={scenes.Sum(item => item.DurationSeconds)}");
Console.WriteLine($"ProjectStatus={project.Status}");
Console.WriteLine($"DialogueSceneCount={scenes.Count(item => !string.IsNullOrWhiteSpace(item.DialogueJson) && item.DialogueJson.Trim() != "[]")}");
Console.WriteLine($"LatestSceneNumber={scenes.LastOrDefault()?.SceneNumber ?? 0}");
Console.WriteLine($"LatestImagePromptLength={scenes.LastOrDefault()?.ImagePrompt.Length ?? 0}");
Console.WriteLine($"LatestVideoPromptLength={scenes.LastOrDefault()?.VideoPrompt.Length ?? 0}");
Console.WriteLine($"LatestDialogueJsonValid={IsValidJson(scenes.LastOrDefault()?.DialogueJson)}");
Console.WriteLine($"DuplicateCount={duplicateCount}");
Console.WriteLine($"NextMissingScene={nextMissing}");
Console.WriteLine($"FourBCallCount={fourBCallCount}");
Console.WriteLine($"OllamaCallCount={ollamaCallCount}");
Console.WriteLine($"InitialCallCount={Math.Min(1, ollamaCallCount)}");
Console.WriteLine($"RepairCalled={ollamaCallCount > 1}");
Console.WriteLine($"RawResponseCharacterCount={responseCharacterCount}");
Console.WriteLine($"ResponseTokenCount={responseTokenCount}");
Console.WriteLine($"StreamDone={streamDone}");
Console.WriteLine($"DoneReason={doneReason}");
Console.WriteLine($"GpuLockReleased={!gpuCoordinator.IsBusy}");
Console.WriteLine($"ElapsedSeconds={(DateTime.Now - startedAt).TotalSeconds:0.0}");
return 0;

static bool IsValidJson(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return false;
    }

    try
    {
        using var _ = System.Text.Json.JsonDocument.Parse(value);
        return true;
    }
    catch (System.Text.Json.JsonException)
    {
        return false;
    }
}

static int ReadMetric(string message, string name)
{
    var value = ReadTextMetric(message, name);
    return int.TryParse(value, out var parsed) ? parsed : 0;
}

static string ReadTextMetric(string message, string name)
{
    var prefix = name + "=";
    var start = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
        return string.Empty;
    }

    start += prefix.Length;
    var end = message.IndexOf(';', start);
    return (end < 0 ? message[start..] : message[start..end]).Trim().TrimEnd('.');
}

file sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

public sealed record StoryGenerationSmokeOptions(bool Write, int? ProjectId, int MaxScenes);

public sealed record StoryGenerationSmokeParseResult(
    bool Success,
    bool ShowHelp,
    StoryGenerationSmokeOptions Options,
    string ErrorMessage)
{
    public static StoryGenerationSmokeParseResult Ok(StoryGenerationSmokeOptions options) =>
        new(true, false, options, string.Empty);

    public static StoryGenerationSmokeParseResult Help() =>
        new(true, true, new StoryGenerationSmokeOptions(false, null, 1), string.Empty);

    public static StoryGenerationSmokeParseResult Fail(string message) =>
        new(false, false, new StoryGenerationSmokeOptions(false, null, 1), message);
}

public static class StoryGenerationSmokeArguments
{
    public static StoryGenerationSmokeParseResult Parse(IReadOnlyList<string> args)
    {
        var write = false;
        var allowMultipleScenes = false;
        int? projectId = null;
        var maxScenes = 1;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg is "--help" or "-h")
            {
                return StoryGenerationSmokeParseResult.Help();
            }

            if (arg == "--write")
            {
                write = true;
                continue;
            }

            if (arg == "--allow-multiple-scenes")
            {
                allowMultipleScenes = true;
                continue;
            }

            if (arg == "--one")
            {
                maxScenes = 1;
                continue;
            }

            if (arg == "--project-id")
            {
                if (!TryReadPositiveInt(args, ref index, arg, out var parsed, out var error))
                {
                    return StoryGenerationSmokeParseResult.Fail(error);
                }

                projectId = parsed;
                continue;
            }

            if (arg == "--max-scenes")
            {
                if (!TryReadPositiveInt(args, ref index, arg, out var parsed, out var error))
                {
                    return StoryGenerationSmokeParseResult.Fail(error);
                }

                maxScenes = parsed;
                continue;
            }

            return StoryGenerationSmokeParseResult.Fail($"Bilinmeyen arguman: {arg}");
        }

        if (write && projectId is null)
        {
            return StoryGenerationSmokeParseResult.Fail("--write icin --project-id <positive integer> zorunludur.");
        }

        if (maxScenes > 1 && !allowMultipleScenes)
        {
            return StoryGenerationSmokeParseResult.Fail("--max-scenes 1'den buyukse --allow-multiple-scenes zorunludur.");
        }

        return StoryGenerationSmokeParseResult.Ok(new StoryGenerationSmokeOptions(write, projectId, maxScenes));
    }

    public static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Usage:");
        writer.WriteLine("  StoryGenerationSmoke --project-id <id>");
        writer.WriteLine("  StoryGenerationSmoke --write --project-id <id> [--max-scenes 1]");
        writer.WriteLine("  StoryGenerationSmoke --write --project-id <id> --max-scenes <n> --allow-multiple-scenes");
        writer.WriteLine();
        writer.WriteLine("Default mode is dry-run. No arguments perform no DB write and no Ollama call.");
    }

    private static bool TryReadPositiveInt(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        out int value,
        out string error)
    {
        value = 0;
        error = string.Empty;
        if (index + 1 >= args.Count)
        {
            error = $"{optionName} icin deger gerekli.";
            return false;
        }

        index++;
        if (!int.TryParse(args[index], out value) || value <= 0)
        {
            error = $"{optionName} positive integer olmali. Deger={args[index]}";
            return false;
        }

        return true;
    }
}

file sealed record SmokeDatabaseLabel(string Provider, string DatabaseName)
{
    public static SmokeDatabaseLabel FromConnectionString(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };
        var database = ReadValue(builder, "Database")
            ?? ReadValue(builder, "Initial Catalog")
            ?? ReadValue(builder, "Data Source")
            ?? "(unknown)";
        return new SmokeDatabaseLabel("SqlServer", Path.GetFileName(database));
    }

    private static string? ReadValue(System.Data.Common.DbConnectionStringBuilder builder, string key) =>
        builder.TryGetValue(key, out var value) ? value?.ToString() : null;
}

file sealed record SmokeProjectSnapshot(
    int ProjectId,
    int SceneCount,
    int FirstMissingScene,
    int DuplicateGroups,
    int CalculatedClipCount,
    string SceneNumbers,
    DateTime ProjectCreatedAt,
    DateTime? ProjectUpdatedAt,
    string SceneTimestampFingerprint)
{
    public static async Task<SmokeProjectSnapshot> LoadAsync(string connectionString, int projectId)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var db = new AppDbContext(options);
        var project = await db.FilmProjects.AsNoTracking().FirstOrDefaultAsync(item => item.Id == projectId)
            ?? throw new InvalidOperationException($"FilmProject bulunamadi. ProjectId={projectId}");
        var sceneNumbers = await db.FilmScenes.AsNoTracking()
            .Where(item => item.FilmProjectId == projectId)
            .OrderBy(item => item.SceneNumber)
            .Select(item => new { item.SceneNumber, item.CreatedAt, item.UpdatedAt })
            .ToListAsync();
        var numbers = sceneNumbers.Select(item => item.SceneNumber).ToList();
        var firstMissing = StoryGenerationService.FindFirstMissingScene(numbers.ToHashSet(), project.CalculatedClipCount);
        var duplicates = numbers.GroupBy(item => item).Count(group => group.Count() > 1);
        var timestampMaterial = string.Join('|', sceneNumbers.Select(item => $"{item.SceneNumber}:{item.CreatedAt:O}:{item.UpdatedAt:O}"));
        var timestampFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(timestampMaterial))).ToLowerInvariant();
        return new SmokeProjectSnapshot(
            projectId,
            numbers.Count,
            firstMissing,
            duplicates,
            project.CalculatedClipCount,
            string.Join(',', numbers),
            project.CreatedAt,
            project.UpdatedAt,
            timestampFingerprint);
    }

    public void WriteTo(TextWriter writer)
    {
        writer.WriteLine($"ExistingSceneCount={SceneCount}");
        writer.WriteLine($"FirstMissingScene={FirstMissingScene}");
        writer.WriteLine($"DuplicateGroups={DuplicateGroups}");
        writer.WriteLine($"CalculatedClipCount={CalculatedClipCount}");
        writer.WriteLine($"SceneNumbers={SceneNumbers}");
        writer.WriteLine($"ProjectCreatedAt={ProjectCreatedAt:O}");
        writer.WriteLine($"ProjectUpdatedAt={ProjectUpdatedAt:O}");
        writer.WriteLine($"SceneTimestampFingerprint={SceneTimestampFingerprint}");
    }
}
