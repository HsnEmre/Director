using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var appSettingsPath = Path.Combine(repoRoot, "Director", "appsettings.json");
if (!File.Exists(appSettingsPath))
{
    repoRoot = Directory.GetCurrentDirectory();
    appSettingsPath = Path.Combine(repoRoot, "Director", "appsettings.json");
}
var diagnosticsRoot = Path.Combine(Path.GetTempPath(), "DirectorWanGpVideoDiagnostics");
Directory.CreateDirectory(diagnosticsRoot);

var configuration = new ConfigurationBuilder()
    .AddJsonFile(appSettingsPath, optional: false, reloadOnChange: false)
    .Build();

var services = new ServiceCollection();
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.Configure<WanGpOptions>(configuration.GetSection("WanGp"));
services.AddSingleton<IWanGpClient, WanGpMcpClient>();
services.AddSingleton<IWanGpLocalModelInventoryService, WanGpLocalModelInventoryService>();
services.AddSingleton<IWanGpVideoInputContractResolver, WanGpVideoInputContractResolver>();
services.AddSingleton<IWanGpVideoTimingContractResolver, WanGpVideoTimingContractResolver>();
services.AddSingleton<ILtxNativeDialogueFinalPromptBuilder, LtxNativeDialogueFinalPromptBuilder>();
services.AddSingleton<IWanGpVideoRequestBuilder, WanGpVideoRequestBuilder>();
services.AddSingleton<IWanGpAudioInputContractResolver, WanGpAudioInputContractResolver>();
services.AddSingleton<IVideoMetadataService, VideoMetadataService>();
services.AddSingleton<IWanGpAudioRequestBuilder, WanGpAudioRequestBuilder>();
services.AddSingleton<IWanGpAudioOutputResolver, WanGpAudioOutputResolver>();

await using var provider = services.BuildServiceProvider();
var options = provider.GetRequiredService<IOptions<WanGpOptions>>().Value;
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

Console.WriteLine($"Endpoint: {options.Endpoint}");
Console.WriteLine($"Diagnostics: {diagnosticsRoot}");

if (args.Length > 0 && string.Equals(args[0], "build-request", StringComparison.OrdinalIgnoreCase))
{
    var imagePath = args.Length > 1 ? args[1] : throw new InvalidOperationException("build-request requires an image path.");
    var builder = provider.GetRequiredService<IWanGpVideoRequestBuilder>();
    var build = await builder.BuildAsync(new WanGpVideoGenerationRequest
    {
        FilmProjectId = 8,
        SceneId = 12,
        SourceImageAssetId = 0,
        SourceImagePath = imagePath,
        ModelType = "ltx2_22B_distilled_gguf_q4_k_m",
        Prompt = "redacted smoke prompt",
        Resolution = "1280x720",
        DurationSeconds = 1,
        InferenceSteps = 8,
        RandomSeed = true,
        InputMode = "start"
    });

    Console.WriteLine($"ImageInputKey={build.ImageInputKey}");
    Console.WriteLine($"InputMode={build.InputModeKey}:{build.InputModeValue}");
    Console.WriteLine($"HasStartImage={build.HasStartImage}");
    return;
}

if (args.Length > 0 && string.Equals(args[0], "audio", StringComparison.OrdinalIgnoreCase))
{
    diagnosticsRoot = Path.Combine(Path.GetTempPath(), "DirectorWanGpAudioDiagnostics");
    Directory.CreateDirectory(diagnosticsRoot);
    var audioModels = await provider.GetRequiredService<IWanGpClient>().GetAvailableAudioModelsAsync();
    WriteJson(Path.Combine(diagnosticsRoot, "all-audio-models.json"), JsonSerializer.SerializeToNode(audioModels, JsonOptions()) ?? new JsonArray());
    var kugel = audioModels.FirstOrDefault(model =>
        model.DisplayName.Contains("KugelAudio", StringComparison.OrdinalIgnoreCase) ||
        model.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"AudioModels={audioModels.Count}");
    if (kugel is null)
    {
        Console.WriteLine("KugelAudio=NOT_FOUND");
        return;
    }

    var schema = await provider.GetRequiredService<IWanGpClient>().GetModelSchemaAsync(kugel.ModelType)
        ?? throw new InvalidOperationException("KugelAudio schema alinamadi.");
    var contract = provider.GetRequiredService<IWanGpAudioInputContractResolver>().Resolve(kugel, schema);
    WriteJson(Path.Combine(diagnosticsRoot, "kugelaudio-models.json"), JsonSerializer.SerializeToNode(new[] { kugel }, JsonOptions()) ?? new JsonArray());
    WriteJson(Path.Combine(diagnosticsRoot, "kugelaudio-schema.json"), RedactNode(schema.RawSchema));
    WriteJson(Path.Combine(diagnosticsRoot, "kugelaudio-defaults.json"), RedactNode(schema.DefaultSettings));
    WriteJson(Path.Combine(diagnosticsRoot, "kugelaudio-contract.json"), JsonSerializer.SerializeToNode(contract, JsonOptions()) ?? new JsonObject());
    WriteJson(Path.Combine(diagnosticsRoot, "kugelaudio-voice-contract.json"), JsonSerializer.SerializeToNode(new
    {
        modelType = kugel.ModelType,
        textKey = contract.TextKey,
        voiceKey = contract.VoiceKey,
        supportsVoicePreset = contract.SupportsVoicePreset,
        usesImplicitDefaultVoice = contract.UsesImplicitDefaultVoice,
        supportsRawReferenceAudio = contract.SupportsRawReferenceAudio,
        seedKey = contract.SeedKey,
        cfgScaleKey = contract.CfgScaleKey,
        doSampleKey = contract.DoSampleKey,
        temperatureKey = contract.TemperatureKey,
        maxNewTokensKey = contract.MaxNewTokensKey,
        voices = contract.AvailableVoices,
        evidence = contract.Evidence
    }, JsonOptions()) ?? new JsonObject());
    var requestBuilder = provider.GetRequiredService<IWanGpAudioRequestBuilder>();
    var requestBuild = await requestBuilder.BuildAsync(new WanGpAudioGenerationRequest
    {
        ModelType = kugel.ModelType,
        TurkishText = "redacted",
        VoicePresetKey = contract.AvailableVoices.FirstOrDefault()?.Key ?? string.Empty,
        Language = "tr",
        CfgScale = 3.0,
        DoSample = false,
        Temperature = 1.0,
        MaxNewTokens = 64,
        InputContract = contract
    });
    WriteJson(Path.Combine(diagnosticsRoot, "kugelaudio-request-summary.json"), JsonSerializer.SerializeToNode(new
    {
        modelType = kugel.ModelType,
        sourceKeys = requestBuild.Source.Keys.OrderBy(key => key).ToArray(),
        hasVoiceKey = !string.IsNullOrWhiteSpace(contract.VoiceKey) && requestBuild.Source.ContainsKey(contract.VoiceKey),
        hasPrompt = requestBuild.Source.ContainsKey(contract.TextKey),
        textHash = requestBuild.TextHash,
        cfgScaleKey = contract.CfgScaleKey,
        doSampleKey = contract.DoSampleKey,
        temperatureKey = contract.TemperatureKey,
        maxNewTokensKey = contract.MaxNewTokensKey
    }, JsonOptions()) ?? new JsonObject());
    Console.WriteLine($"KugelModelType={kugel.ModelType}");
    Console.WriteLine($"KugelDisplayName={kugel.DisplayName}");
    Console.WriteLine($"ContractValidated={contract.IsValidated}");
    Console.WriteLine($"TextKey={contract.TextKey}");
    Console.WriteLine($"VoiceKey={contract.VoiceKey}");
    Console.WriteLine($"VoiceCount={contract.AvailableVoices.Count}");
    Console.WriteLine($"ImplicitDefaultVoice={contract.UsesImplicitDefaultVoice}");
    Console.WriteLine($"RawReferenceAudio={contract.SupportsRawReferenceAudio}");
    foreach (var voice in contract.AvailableVoices.Take(10))
    {
        Console.WriteLine($"Voice={voice.Key}");
    }

    return;
}

if (args.Length > 0 && string.Equals(args[0], "audio-generate", StringComparison.OrdinalIgnoreCase))
{
    var text = args.Length > 1 ? args[1] : "Merhaba, bugun birlikte yeni bir maceraya cikiyoruz.";
    var audioClient = provider.GetRequiredService<IWanGpClient>();
    var audioModels = await audioClient.GetAvailableAudioModelsAsync();
    var kugel = audioModels.FirstOrDefault(model =>
        model.DisplayName.Contains("KugelAudio", StringComparison.OrdinalIgnoreCase) ||
        model.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("KugelAudio modeli bulunamadi.");
    var schema = await audioClient.GetModelSchemaAsync(kugel.ModelType)
        ?? throw new InvalidOperationException("KugelAudio schema alinamadi.");
    var contract = provider.GetRequiredService<IWanGpAudioInputContractResolver>().Resolve(kugel, schema);
    var voice = contract.AvailableVoices.FirstOrDefault()
        ?? throw new InvalidOperationException("KugelAudio varsayilan ses sozlesmesi bulunamadi.");
    var builder = provider.GetRequiredService<IWanGpAudioRequestBuilder>();
    var outputResolver = provider.GetRequiredService<IWanGpAudioOutputResolver>();
    var before = outputResolver.CaptureSnapshot();
    var startedAt = DateTime.Now;
    var build = await builder.BuildAsync(new WanGpAudioGenerationRequest
    {
        ModelType = kugel.ModelType,
        TurkishText = text,
        VoicePresetKey = voice.Key,
        Language = "tr",
        CfgScale = 3.0,
        TargetDurationSeconds = 5,
        InputContract = contract
    });
    var submission = await audioClient.SubmitAudioGenerationAsync(build.Source);
    Console.WriteLine($"SubmittedJobId={submission.ExternalJobId}");
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(12));
    while (true)
    {
        var snapshot = await audioClient.GetJobAsync(submission.ExternalJobId, timeout.Token);
        var explicitPaths = snapshot.GeneratedFiles.ToList();
        if (!string.IsNullOrWhiteSpace(snapshot.OutputPath))
        {
            explicitPaths.Add(snapshot.OutputPath);
        }

        var output = await outputResolver.ResolveAudioOutputsAsync(before, startedAt, explicitPaths, TimeSpan.FromSeconds(1), timeout.Token);
        if (output.Success)
        {
            var candidate = output.Candidates.First();
            Console.WriteLine($"GeneratedAudio={candidate.FilePath}");
            Console.WriteLine($"GeneratedAudioSize={candidate.FileSize}");
            return;
        }

        Console.WriteLine($"JobStatus={snapshot.Status}; Progress={snapshot.ProgressPercentage:0.0}; Phase={snapshot.Phase}");
        if (snapshot.Status is Director.Enums.GenerationJobStatus.Completed or Director.Enums.GenerationJobStatus.Failed or Director.Enums.GenerationJobStatus.Cancelled or Director.Enums.GenerationJobStatus.Interrupted)
        {
            throw new InvalidOperationException($"Audio generation ended without resolved output. Status={snapshot.Status}; Message={snapshot.Message}");
        }

        await Task.Delay(2000, timeout.Token);
    }
}

if (args.Length > 0 && string.Equals(args[0], "ltx-native", StringComparison.OrdinalIgnoreCase))
{
    diagnosticsRoot = Path.Combine(Path.GetTempPath(), "DirectorWanGpNativeDialogueDiagnostics");
    Directory.CreateDirectory(diagnosticsRoot);
    var modelType = "ltx2_22B_distilled_gguf_q4_k_m";
    var ltxClient = provider.GetRequiredService<IWanGpClient>();
    var schema = await ltxClient.GetModelSchemaAsync(modelType)
        ?? throw new InvalidOperationException("LTX schema alinamadi.");
    WriteJson(Path.Combine(diagnosticsRoot, "ltx-native-dialogue-schema.json"), RedactNode(schema.RawSchema));
    WriteJson(Path.Combine(diagnosticsRoot, "ltx-native-dialogue-defaults.json"), RedactNode(schema.DefaultSettings));
    var exportSummary = new JsonObject
    {
        ["modelType"] = modelType,
        ["hasAudioRelatedKeys"] = schema.RawSchema.ToJsonString().Contains("audio", StringComparison.OrdinalIgnoreCase) ||
            schema.DefaultSettings.ToJsonString().Contains("audio", StringComparison.OrdinalIgnoreCase),
        ["hasImageStart"] = schema.RawSchema.ToJsonString().Contains("image_start", StringComparison.OrdinalIgnoreCase) ||
            schema.DefaultSettings.ToJsonString().Contains("image_start", StringComparison.OrdinalIgnoreCase),
        ["hasImagePromptType"] = schema.RawSchema.ToJsonString().Contains("image_prompt_type", StringComparison.OrdinalIgnoreCase) ||
            schema.DefaultSettings.ToJsonString().Contains("image_prompt_type", StringComparison.OrdinalIgnoreCase),
        ["defaultSettingsKeys"] = new JsonArray(schema.DefaultSettings.Select(pair => JsonValue.Create(pair.Key)).ToArray())
    };
    WriteJson(Path.Combine(diagnosticsRoot, "ltx-native-dialogue-export-summary.json"), exportSummary);

    var tempImage = Path.Combine(Path.GetTempPath(), $"director_ltx_native_smoke_{Guid.NewGuid():N}.png");
    File.WriteAllBytes(tempImage, [1, 2, 3, 4]);
    try
    {
        var builder = provider.GetRequiredService<IWanGpVideoRequestBuilder>();
        var build = await builder.BuildAsync(new WanGpVideoGenerationRequest
        {
            ModelType = modelType,
            SourceImagePath = tempImage,
            SourceImageAssetId = 1,
            SceneId = 1,
            Prompt = "Single continuous shot. A character speaks audibly in Turkish. No subtitles.",
            Resolution = "1280x720",
            DurationSeconds = 10,
            InferenceSteps = 8,
            RandomSeed = true,
            InputMode = "start",
            GenerationMode = Director.Enums.VideoAudioGenerationMode.LtxNativeDialogue,
            DialogueSourceHash = new string('b', 64),
            DialogueCount = 1,
            SpeakerCount = 1
        });
        WriteJson(Path.Combine(diagnosticsRoot, "ltx-native-dialogue-request-summary.json"), JsonSerializer.SerializeToNode(new
        {
            modelType,
            sourceKeys = build.Source.Keys.OrderBy(key => key).ToArray(),
            build.ImageInputKey,
            build.InputModeKey,
            build.InputModeValue,
            nativeAudioRequired = build.NativeAudioRequired,
            nativeAudioDisabledByRequest = build.NativeAudioDisabledByRequest,
            durationKey = build.TimingContract?.DurationKey,
            durationUnit = build.TimingContract?.DurationUnit.ToString(),
            frameCount = build.TimingContract?.CalculatedFrameCount,
            fps = build.TimingContract?.SelectedFps,
            hasPrompt = build.Source.ContainsKey("prompt")
        }, JsonOptions()) ?? new JsonObject());
        Console.WriteLine($"LtxNativeDiagnostics={diagnosticsRoot}");
        Console.WriteLine($"ImageInputKey={build.ImageInputKey}");
        Console.WriteLine($"InputMode={build.InputModeKey}:{build.InputModeValue}");
        Console.WriteLine($"NativeAudioDisabledByRequest={build.NativeAudioDisabledByRequest}");
        Console.WriteLine($"FrameCount={build.TimingContract?.CalculatedFrameCount}");
    }
    finally
    {
        try { File.Delete(tempImage); } catch { }
    }

    return;
}

var all = await CallListModelsAsync(options.Endpoint, loggerFactory, new Dictionary<string, object?>
{
    ["include_availability"] = true
});
WriteJson(Path.Combine(diagnosticsRoot, "all-models.json"), all);

var video = await CallListModelsAsync(options.Endpoint, loggerFactory, new Dictionary<string, object?>
{
    ["main_output"] = "video",
    ["include_availability"] = true
});
WriteJson(Path.Combine(diagnosticsRoot, "video-models.json"), video);

var videoImage = await CallListModelsAsync(options.Endpoint, loggerFactory, new Dictionary<string, object?>
{
    ["main_output"] = "video",
    ["inputs"] = "image",
    ["include_availability"] = true
});
WriteJson(Path.Combine(diagnosticsRoot, "video-image-input-models.json"), videoImage);

var ltxModels = FindModels(all, "ltx").Concat(FindModels(video, "ltx")).Concat(FindModels(videoImage, "ltx"))
    .OfType<JsonObject>()
    .GroupBy(model => ReadString(model, "model_type", "modelType", "type", "name"), StringComparer.OrdinalIgnoreCase)
    .Select(group => group.First())
    .ToList();

var ltxReport = new JsonArray();
foreach (var model in ltxModels)
{
    var modelType = ReadString(model, "model_type", "modelType", "type", "name");
    var entry = new JsonObject
    {
        ["model"] = model.DeepClone(),
        ["schema"] = await CallOptionalAsync(options.Endpoint, loggerFactory, "wangp_get_model_schema", new Dictionary<string, object?> { ["model_type"] = modelType }),
        ["defaultSettings"] = await CallOptionalAsync(options.Endpoint, loggerFactory, "wangp_get_default_settings", new Dictionary<string, object?> { ["model_type"] = modelType }),
        ["availability"] = await CallOptionalAsync(options.Endpoint, loggerFactory, "wangp_get_model_availability", new Dictionary<string, object?> { ["model_type"] = modelType })
    };
    ltxReport.Add(entry);
}

WriteJson(Path.Combine(diagnosticsRoot, "ltx-models.json"), ltxReport);

var client = provider.GetRequiredService<IWanGpClient>();
var discovered = await client.GetAvailableImageToVideoModelsAsync();
var inventory = await provider.GetRequiredService<IWanGpLocalModelInventoryService>().GetInventoryAsync(discovered, forceRefresh: true);

Console.WriteLine($"All models: {CountModels(all)}");
Console.WriteLine($"Video models: {CountModels(video)}");
Console.WriteLine($"Video image-input models: {CountModels(videoImage)}");
Console.WriteLine($"LTX models: {ltxModels.Count}");
Console.WriteLine($"Director I2V models: {discovered.Count}");
foreach (var model in discovered)
{
    inventory.TryGetValue(model.ModelType, out var item);
    Console.WriteLine($"{model.ModelType} | {model.DisplayName} | availability={model.Availability} | inventory={item?.Status} | checkpoint={item?.CheckpointPath}");
}

static async Task<JsonNode> CallListModelsAsync(string endpoint, ILoggerFactory loggerFactory, IReadOnlyDictionary<string, object?> args)
{
    return await CallToolAsync(endpoint, loggerFactory, "wangp_list_models", args);
}

static async Task<JsonNode?> CallOptionalAsync(string endpoint, ILoggerFactory loggerFactory, string toolName, IReadOnlyDictionary<string, object?> args)
{
    try
    {
        return await CallToolAsync(endpoint, loggerFactory, toolName, args);
    }
    catch (Exception ex)
    {
        return new JsonObject { ["error"] = ex.Message };
    }
}

static async Task<JsonNode> CallToolAsync(string endpoint, ILoggerFactory loggerFactory, string toolName, IReadOnlyDictionary<string, object?> args)
{
    await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
    {
        Endpoint = new Uri(endpoint),
        Name = "WanGP discovery smoke",
        TransportMode = HttpTransportMode.StreamableHttp,
        ConnectionTimeout = TimeSpan.FromSeconds(15)
    }, loggerFactory), loggerFactory: loggerFactory);

    var result = await client.CallToolAsync(toolName, args!);
    if (result.IsError == true)
    {
        throw new InvalidOperationException($"WanGP MCP tool failed: {toolName}");
    }

    if (result.StructuredContent is not null)
    {
        return JsonSerializer.SerializeToNode(result.StructuredContent, JsonOptions()) ?? new JsonObject();
    }

    return JsonSerializer.SerializeToNode(result, JsonOptions()) ?? new JsonObject();
}

static IEnumerable<JsonNode?> FindModels(JsonNode node, string term)
{
    return ExtractModelArray(node)
        .Where(item => item?.ToJsonString().Contains(term, StringComparison.OrdinalIgnoreCase) == true);
}

static int CountModels(JsonNode node)
{
    return ExtractModelArray(node).Count();
}

static IEnumerable<JsonNode?> ExtractModelArray(JsonNode node)
{
    if (node is JsonArray array)
    {
        return array;
    }

    if (node is JsonObject obj)
    {
        foreach (var key in new[] { "models", "result", "data", "structuredContent", "payload" })
        {
            if (obj.TryGetPropertyValue(key, out var child) && child is JsonArray childArray)
            {
                return childArray;
            }

            if (child is JsonObject childObject && childObject.TryGetPropertyValue("models", out var models) && models is JsonArray modelsArray)
            {
                return modelsArray;
            }
        }
    }

    return [];
}

static string ReadString(JsonObject obj, params string[] keys)
{
    foreach (var key in keys)
    {
        if (obj.TryGetPropertyValue(key, out var node) && node is not null)
        {
            return node.ToString();
        }
    }

    return string.Empty;
}

static void WriteJson(string path, JsonNode node)
{
    File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}

static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { WriteIndented = true };

static JsonNode RedactNode(JsonNode node)
{
    if (node is JsonObject obj)
    {
        var copy = new JsonObject();
        foreach (var pair in obj)
        {
            copy[pair.Key] = ShouldRedact(pair.Key)
                ? RedactValue(pair.Value)
                : pair.Value is null ? null : RedactNode(pair.Value);
        }

        return copy;
    }

    if (node is JsonArray array)
    {
        var copy = new JsonArray();
        foreach (var item in array)
        {
            copy.Add(item is null ? null : RedactNode(item));
        }

        return copy;
    }

    return node.DeepClone();
}

static bool ShouldRedact(string key)
{
    return key.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("text", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("script", StringComparison.OrdinalIgnoreCase);
}

static JsonNode RedactValue(JsonNode? node)
{
    var value = node?.ToString() ?? string.Empty;
    return new JsonObject
    {
        ["redacted"] = true,
        ["length"] = value.Length,
        ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()
    };
}
