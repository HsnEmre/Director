using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Options;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var appSettingsPath = Path.Combine(repoRoot, "Director", "appsettings.json");
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

await using var provider = services.BuildServiceProvider();
var options = provider.GetRequiredService<IOptions<WanGpOptions>>().Value;
var loggerFactory = provider.GetRequiredService<ILoggerFactory>();

Console.WriteLine($"Endpoint: {options.Endpoint}");
Console.WriteLine($"Diagnostics: {diagnosticsRoot}");

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
