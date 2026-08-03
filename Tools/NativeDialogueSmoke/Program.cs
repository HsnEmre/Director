using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Director.Data;
using Director.Enums;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

const int ProjectId = 9;
const int SceneId = 36;
var execute = args.Length == 1 && args[0] == "--execute";
if (args.Length > 0 && !execute)
{
    Console.Error.WriteLine("Usage: NativeDialogueSmoke [--execute]");
    return 2;
}

Console.OutputEncoding = Encoding.UTF8;
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var configuration = new ConfigurationBuilder()
    .SetBasePath(Path.Combine(root, "Director"))
    .AddJsonFile("appsettings.json", optional: false)
    .Build();
var connectionString = configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection bulunamadı.");
var writeGuard = new WriteGuardInterceptor();

var services = new ServiceCollection();
services.Configure<OllamaOptions>(configuration.GetSection("Ollama"));
services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton(writeGuard);
services.AddDbContextFactory<AppDbContext>((provider, options) =>
    options.UseSqlServer(connectionString).AddInterceptors(provider.GetRequiredService<WriteGuardInterceptor>()));
services.AddHttpClient<OllamaClient>()
    .ConfigurePrimaryHttpMessageHandler(provider => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1,
            provider.GetRequiredService<IOptions<OllamaOptions>>().Value.SceneConnectTimeoutSeconds))
    });
services.AddSingleton<CountingOllamaClient>();
services.AddSingleton<IOllamaClient>(provider => provider.GetRequiredService<CountingOllamaClient>());
services.AddSingleton<IOllamaFailureDiagnosticWriter, OllamaFailureDiagnosticWriter>();
services.AddSingleton<IGpuGenerationCoordinator, GpuGenerationCoordinator>();
services.AddSingleton<ILtxNativeDialogueFinalPromptBuilder, LtxNativeDialogueFinalPromptBuilder>();
services.AddSingleton<ILtxNativeDialoguePromptComposer, LtxNativeDialoguePromptComposer>();

await using var provider = services.BuildServiceProvider();
var dbFactory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
var before = await Snapshot.LoadAsync(dbFactory, ProjectId, SceneId);
Console.WriteLine($"ProjectId={ProjectId}");
before.WriteTo(Console.Out, "Before");

if (!execute)
{
    Console.WriteLine("Mode=InspectOnly");
    Console.WriteLine("OllamaCallCount=0");
    Console.WriteLine("WanGpSubmitCount=0");
    Console.WriteLine("DBWriteCount=0");
    return 0;
}

Console.WriteLine("Mode=ExecuteReadOnlyComposer");
var selectedReferenceId = before.SelectedReferenceId
    ?? throw new InvalidOperationException("Scene 36 için seçili görsel bulunamadı.");
var composer = provider.GetRequiredService<ILtxNativeDialoguePromptComposer>();
var counter = provider.GetRequiredService<CountingOllamaClient>();
var gpu = provider.GetRequiredService<IGpuGenerationCoordinator>();
var exitCode = 0;
try
{
    var result = await composer.BuildReadOnlyAsync(SceneId, selectedReferenceId, allowRepair: false);
    Console.WriteLine("ComposerResult=Success");
    WriteResult(result);
}
catch (NativeDialoguePromptCompositionException exception)
{
    exitCode = 1;
    Console.WriteLine("ComposerResult=Failure");
    Console.WriteLine($"FailureStage={exception.FailureStage}");
    Console.WriteLine($"SafeReason={exception.SafeReason}");
    Console.WriteLine($"CharacterKey={exception.CharacterKey ?? string.Empty}");
    Console.WriteLine($"DiagnosticPath={exception.DiagnosticPath ?? string.Empty}");
    WriteDiagnosticSummary(exception.DiagnosticPath);
    Console.WriteLine("FinalNativePromptCharacterCount=0");
    Console.WriteLine("RepairUsed=False");
}

var after = await Snapshot.LoadAsync(dbFactory, ProjectId, SceneId);
after.WriteTo(Console.Out, "After");
Console.WriteLine($"OllamaCallCount={counter.CallCount}");
Console.WriteLine($"FourBCallCount={counter.FourBCallCount}");
Console.WriteLine($"WanGpSubmitCount=0");
Console.WriteLine($"DBWriteCount={writeGuard.WriteCount}");
Console.WriteLine($"GpuLockReleased={!gpu.IsBusy}");
Console.WriteLine($"DatabaseUnchanged={before.IntegrityFingerprint == after.IntegrityFingerprint}");
return exitCode;

static void WriteResult(LtxNativeDialoguePromptResult result)
{
    Console.WriteLine($"Model={result.Model}");
    Console.WriteLine($"PromptTokenCount={result.PromptTokenCount}");
    Console.WriteLine($"ResponseTokenCount={result.ResponseTokenCount}");
    Console.WriteLine($"ResponseCharacterCount={result.ResponseCharacterCount}");
    Console.WriteLine($"Done={result.Done}");
    Console.WriteLine($"DoneReason={result.DoneReason}");
    Console.WriteLine($"RawResponseShape={result.RawResponseShape}");
    Console.WriteLine($"ParseStage={result.ParseStage}");
    Console.WriteLine($"ValidationResult={result.ValidationResult}");
    Console.WriteLine($"RepairUsed={result.RepairUsed}");
    Console.WriteLine($"SpeakerKey={result.SpeakerKey}");
    Console.WriteLine($"ExactDialogue={result.ExactDialogue}");
    Console.WriteLine($"VoiceProfileSource={result.VoiceProfileSource}");
    Console.WriteLine($"DialogueEntryCount={result.DialogueCount}");
    Console.WriteLine($"ResolvedSpeakerCount={result.SpeakerCount}");
    Console.WriteLine($"FinalNativePromptCharacterCount={result.CombinedPrompt.Length}");
    Console.WriteLine($"DeterministicFinalPromptAssemblyPass={result.IsValid && result.CombinedPrompt.Length > 0}");
    Console.WriteLine($"NamedSpeakerCanonicalLiteralPass={result.NamedSpeakerCanonicalLines.Count > 0 && result.NamedSpeakerCanonicalLines.All(line => result.CombinedPrompt.Contains(line, StringComparison.Ordinal))}");
    Console.WriteLine($"OnlySpeakerCanonicalLiteralPass={!string.IsNullOrWhiteSpace(result.OnlySpeakerCanonicalLine) && result.CombinedPrompt.Contains(result.OnlySpeakerCanonicalLine, StringComparison.Ordinal)}");
    Console.WriteLine($"AuthoritativeExactDialoguePass={result.ExactSpokenLines.Count == result.DialogueCount}");
    Console.WriteLine($"ModelReturnedCombinedPrompt={result.ModelReturnedCombinedPrompt}");
    Console.WriteLine($"DiagnosticPath={result.DiagnosticPath}");
}

static void WriteDiagnosticSummary(string? path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var root = document.RootElement;
    string Text(string name) => root.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;
    var raw = Text("assembledRawResponse");
    var shape = string.IsNullOrWhiteSpace(raw) ? "Empty" : raw.TrimStart('\uFEFF').TrimStart().StartsWith("```")
        ? "CodeFence" : raw.TrimStart('\uFEFF').TrimStart().StartsWith('{') ? "JsonObject" : raw.Contains('{') ? "ExplanationWithJson" : "PlainText";
    Console.WriteLine($"Model={Text("selectedModel")}");
    Console.WriteLine($"PromptTokenCount={Text("promptTokenCount")}");
    Console.WriteLine($"ResponseTokenCount={Text("responseTokenCount")}");
    Console.WriteLine($"ResponseCharacterCount={Text("responseCharacterCount")}");
    Console.WriteLine($"Done={Text("done")}");
    Console.WriteLine($"DoneReason={Text("doneReason")}");
    Console.WriteLine($"RawResponseShape={shape}");
    Console.WriteLine($"ParseStage={Text("failureStage")}");
    Console.WriteLine($"ValidationErrors={Text("validationErrors")}");
}

file sealed class CountingOllamaClient(OllamaClient inner) : IOllamaClient
{
    public int CallCount { get; private set; }
    public int FourBCallCount { get; private set; }

    public Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        inner.CheckHealthAsync(cancellationToken);

    public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default) =>
        inner.IsModelAvailableAsync(modelName, cancellationToken);

    public Task<TResponse> ChatStructuredAsync<TResponse>(IReadOnlyList<OllamaChatMessage> messages, object jsonSchema,
        string? modelOverride = null, TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default,
        IProgress<OllamaStreamProgress>? streamProgress = null, OllamaGenerationSettings? generationSettings = null)
    {
        Count(modelOverride);
        return inner.ChatStructuredAsync<TResponse>(messages, jsonSchema, modelOverride, requestTimeout,
            cancellationToken, streamProgress, generationSettings);
    }

    public Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages, object jsonSchema, string? modelOverride = null,
        TimeSpan? requestTimeout = null, CancellationToken cancellationToken = default,
        IProgress<OllamaStreamProgress>? streamProgress = null, OllamaGenerationSettings? generationSettings = null)
    {
        Count(modelOverride);
        return inner.ChatStructuredDetailedAsync<TResponse>(messages, jsonSchema, modelOverride, requestTimeout,
            cancellationToken, streamProgress, generationSettings);
    }

    private void Count(string? model)
    {
        CallCount++;
        if (model?.Contains("4b", StringComparison.OrdinalIgnoreCase) == true) FourBCallCount++;
    }
}

file sealed class WriteGuardInterceptor : SaveChangesInterceptor
{
    public int WriteCount { get; private set; }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        WriteCount++;
        throw new InvalidOperationException("NativeDialogueSmoke DB yazma koruması SaveChanges çağrısını engelledi.");
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        WriteCount++;
        throw new InvalidOperationException("NativeDialogueSmoke DB yazma koruması SaveChangesAsync çağrısını engelledi.");
    }
}

file sealed record Snapshot(
    int SceneCount,
    int AppliedMigrationCount,
    string SceneNumbers,
    int DuplicateSceneNumberGroups,
    int SceneId,
    int SceneNumber,
    string SceneTitle,
    string DialogueJson,
    bool DialogueJsonValid,
    int DialogueCount,
    string DialogueSpeakerKeys,
    string CharactersJson,
    string StoryCharacterKeys,
    string CharacterMatches,
    string Narration,
    int? SelectedReferenceId,
    string SelectedReferencePath,
    string VoiceProfiles,
    int SceneJobCount,
    string SceneJobStatuses,
    int FailedJobCount,
    int ActiveJobCount,
    int VideoAssetCount,
    string IntegrityFingerprint)
{
    public static async Task<Snapshot> LoadAsync(IDbContextFactory<AppDbContext> factory, int projectId, int sceneId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var scene = await db.FilmScenes.AsNoTracking().SingleAsync(item => item.Id == sceneId && item.FilmProjectId == projectId);
        var appliedMigrationCount = (await db.Database.GetAppliedMigrationsAsync()).Count();
        var storyCharacters = await db.StoryCharacters.AsNoTracking()
            .Where(item => item.FilmStoryId == scene.FilmStoryId).OrderBy(item => item.SortOrder)
            .Select(item => new { item.Id, item.CharacterKey, item.Name }).ToListAsync();
        var selected = await db.SceneMediaAssets.AsNoTracking()
            .Where(item => item.SceneId == sceneId && item.MediaType == MediaType.Image && item.IsSelected)
            .OrderByDescending(item => item.VersionNumber).FirstOrDefaultAsync();
        var profiles = await db.LtxNativeVoiceProfiles.AsNoTracking()
            .Where(item => item.FilmProjectId == projectId)
            .OrderBy(item => item.StoryCharacterId).ToListAsync();
        var jobs = await db.GenerationJobs.AsNoTracking().Where(item => item.SceneId == sceneId).ToListAsync();
        var videoAssetCount = await db.SceneMediaAssets.AsNoTracking()
            .CountAsync(item => item.SceneId == sceneId && item.MediaType == MediaType.Video);
        var sceneRows = await db.FilmScenes.AsNoTracking().Where(item => item.FilmProjectId == projectId)
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.SceneNumber, item.UpdatedAt }).ToListAsync();
        var allJobCount = await db.GenerationJobs.AsNoTracking().CountAsync(item => item.FilmProjectId == projectId);
        var allAssetCount = await db.SceneMediaAssets.AsNoTracking().CountAsync(item => item.FilmProjectId == projectId);
        var profileSummary = string.Join(" | ", profiles.Select(item =>
            $"Id={item.Id},CharacterId={item.StoryCharacterId},RequiredFieldsValid={VoiceProfileValid(item)}"));
        var (valid, dialogueCount, speakerKeys) = ReadDialogue(scene.DialogueJson);
        var storyKeys = string.Join(",", storyCharacters.Select(item => $"{item.CharacterKey}:{item.Name}(Id={item.Id})"));
        var matches = string.Join(",", speakerKeys.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(key =>
        {
            var found = storyCharacters.Where(item => string.Equals(item.CharacterKey, key, StringComparison.OrdinalIgnoreCase)).ToList();
            return $"{key}=>{(found.Count == 1 ? found[0].Id : found.Count == 0 ? "none" : "ambiguous")}";
        }));
        var sceneNumbers = string.Join(',', sceneRows.Select(item => item.SceneNumber));
        var duplicateGroups = sceneRows.GroupBy(item => item.SceneNumber).Count(group => group.Count() > 1);
        var jobStatuses = string.Join(" | ", jobs.OrderBy(item => item.Id)
            .Select(item => $"Id={item.Id},Status={item.Status},Phase={item.CurrentPhase},ExternalJobId={item.ExternalJobId ?? string.Empty}"));
        var fingerprintSource = JsonSerializer.Serialize(new
        {
            Scenes = sceneRows,
            AppliedMigrationCount = appliedMigrationCount,
            JobCount = allJobCount,
            AssetCount = allAssetCount,
            SelectedReference = selected is null ? null : new { selected.Id, selected.FilePath, selected.IsSelected },
            Profiles = profiles.Select(item => new { item.Id, item.UpdatedAt, item.SettingsHash })
        });
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new Snapshot(sceneRows.Count, appliedMigrationCount, sceneNumbers, duplicateGroups, scene.Id, scene.SceneNumber, scene.Title, scene.DialogueJson, valid,
            dialogueCount, speakerKeys, scene.CharactersJson, storyKeys, matches, scene.NarrationText,
            selected?.Id, selected?.FilePath ?? string.Empty, profileSummary, jobs.Count, jobStatuses,
            jobs.Count(item => item.Status == GenerationJobStatus.Failed),
            jobs.Count(item => item.Status is GenerationJobStatus.Pending or GenerationJobStatus.Queued or GenerationJobStatus.Running ||
                item.CurrentPhase.Contains("Preparing", StringComparison.OrdinalIgnoreCase)),
            videoAssetCount, fingerprint);
    }

    public void WriteTo(TextWriter writer, string prefix)
    {
        writer.WriteLine($"{prefix}SceneCount={SceneCount}");
        writer.WriteLine($"{prefix}AppliedMigrationCount={AppliedMigrationCount}");
        writer.WriteLine($"{prefix}SceneNumbers={SceneNumbers}");
        writer.WriteLine($"{prefix}DuplicateSceneNumberGroups={DuplicateSceneNumberGroups}");
        writer.WriteLine($"{prefix}SceneId={SceneId}");
        writer.WriteLine($"{prefix}SceneNumber={SceneNumber}");
        writer.WriteLine($"{prefix}SceneTitle={SceneTitle}");
        writer.WriteLine($"{prefix}DialogueJsonLength={DialogueJson.Length}");
        writer.WriteLine($"{prefix}DialogueJson={DialogueJson}");
        writer.WriteLine($"{prefix}DialogueJsonValid={DialogueJsonValid}");
        writer.WriteLine($"{prefix}DialogueCount={DialogueCount}");
        writer.WriteLine($"{prefix}DialogueSpeakerKeys={DialogueSpeakerKeys}");
        writer.WriteLine($"{prefix}CharactersJson={CharactersJson}");
        writer.WriteLine($"{prefix}StoryCharacterKeys={StoryCharacterKeys}");
        writer.WriteLine($"{prefix}CharacterMatches={CharacterMatches}");
        writer.WriteLine($"{prefix}Narration={Narration}");
        writer.WriteLine($"{prefix}SelectedReferenceId={SelectedReferenceId?.ToString() ?? string.Empty}");
        writer.WriteLine($"{prefix}SelectedReferencePath={SelectedReferencePath}");
        writer.WriteLine($"{prefix}VoiceProfiles={VoiceProfiles}");
        writer.WriteLine($"{prefix}SceneJobCount={SceneJobCount}");
        writer.WriteLine($"{prefix}SceneJobStatuses={SceneJobStatuses}");
        writer.WriteLine($"{prefix}FailedJobCount={FailedJobCount}");
        writer.WriteLine($"{prefix}ActiveJobCount={ActiveJobCount}");
        writer.WriteLine($"{prefix}VideoAssetCount={VideoAssetCount}");
        writer.WriteLine($"{prefix}IntegrityFingerprint={IntegrityFingerprint}");
    }

    private static (bool Valid, int Count, string SpeakerKeys) ReadDialogue(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return (false, 0, string.Empty);
            var keys = document.RootElement.EnumerateArray().Select(item =>
                item.TryGetProperty("speakerKey", out var key) ? key.GetString() :
                item.TryGetProperty("characterKey", out var characterKey) ? characterKey.GetString() : string.Empty).ToList();
            return (true, keys.Count, string.Join(',', keys));
        }
        catch (JsonException)
        {
            return (false, 0, string.Empty);
        }
    }

    private static bool VoiceProfileValid(Director.Models.LtxNativeVoiceProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.VoiceDescription) &&
        !string.IsNullOrWhiteSpace(profile.Language) &&
        !string.IsNullOrWhiteSpace(profile.SpeakingStyle) &&
        !string.IsNullOrWhiteSpace(profile.PerceivedAge) &&
        !string.IsNullOrWhiteSpace(profile.GenderPresentation) &&
        !string.IsNullOrWhiteSpace(profile.AccentDescription) &&
        !string.IsNullOrWhiteSpace(profile.PitchDescription) &&
        !string.IsNullOrWhiteSpace(profile.TempoDescription) &&
        !string.IsNullOrWhiteSpace(profile.SettingsHash);
}
