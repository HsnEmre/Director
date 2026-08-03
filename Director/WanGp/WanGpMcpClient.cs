using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using Director.Enums;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Director.WanGp;

public sealed class WanGpMcpClient : IWanGpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> GeneratedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv", ".wav", ".flac", ".mp3", ".ogg", ".m4a"
    };
    private readonly WanGpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WanGpMcpClient> _logger;

    public WanGpMcpClient(
        IOptions<WanGpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<WanGpMcpClient> logger)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<WanGpConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var client = await CreateClientAsync(cancellationToken);
            await client.PingAsync(cancellationToken: cancellationToken);
            return new WanGpConnectionResult { IsAvailable = true, Message = "WanGP MCP bağlantısı başarılı." };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WanGP MCP bağlantısı kurulamadı.");
            return new WanGpConnectionResult { IsAvailable = false, Message = "WanGP bağlantısı kurulamadı." };
        }
    }

    public async Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageModelsAsync(CancellationToken cancellationToken = default)
    {
        var node = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?>
        {
            ["main_output"] = "image"
        }, cancellationToken);

        var modelsNode = ReadArray(node, "models", "result") ?? node as JsonArray ?? new JsonArray();
        var models = new List<WanGpModelInfo>();
        foreach (var item in modelsNode)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var info = new WanGpModelInfo
            {
                ModelType = ReadString(obj, "model_type", "modelType", "type", "name"),
                DisplayName = ReadString(obj, "display_name", "displayName", "name", "model_type"),
                Availability = ReadString(obj, "availability", "status"),
                MainOutput = ReadString(obj, "main_output", "mainOutput"),
                Family = ReadString(obj, "family"),
                Inputs = ReadString(obj, "inputs", "input_types"),
                RawMetadata = obj.DeepClone() as JsonObject ?? new JsonObject()
            };

            if (SupportsValue(obj, "image", "main_output", "mainOutput") || string.IsNullOrWhiteSpace(info.MainOutput))
            {
                models.Add(info);
            }
        }

        return models;
    }

    public async Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageToVideoModelsAsync(CancellationToken cancellationToken = default)
    {
        var node = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?>
        {
            ["main_output"] = "video",
            ["inputs"] = "image",
            ["include_availability"] = true
        }, cancellationToken);

        var modelsNode = ReadArray(node, "models", "result") ?? node as JsonArray ?? new JsonArray();
        var models = new List<WanGpModelInfo>();
        foreach (var item in modelsNode)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var info = new WanGpModelInfo
            {
                ModelType = ReadString(obj, "model_type", "modelType", "type", "name"),
                DisplayName = ReadString(obj, "display_name", "displayName", "name", "model_type"),
                Availability = ReadString(obj, "availability", "status"),
                MainOutput = ReadString(obj, "main_output", "mainOutput", "outputs"),
                Family = ReadString(obj, "family"),
                Architecture = ReadString(obj, "architecture"),
                BaseModelType = ReadString(obj, "base_model_type", "baseModelType"),
                Outputs = ReadString(obj, "outputs"),
                Inputs = ReadString(obj, "inputs", "input_types", "media_inputs"),
                RawMetadata = obj.DeepClone() as JsonObject ?? new JsonObject()
            };

            var json = obj.ToJsonString();
            var outputsVideo = SupportsValue(obj, "video", "main_output", "mainOutput", "outputs") ||
                HasKeyValue(obj, "output", "video") ||
                HasKeyValue(obj, "outputs", "video");
            var inputsImage = SupportsValue(obj, "image", "inputs", "input_types") ||
                HasKeyValue(obj, "input", "image") ||
                HasKeyValue(obj, "inputs", "image");
            var imageToVideo = HasKeyValue(obj, "image_to_video", "true") ||
                json.Contains("\"image_to_video\":true", StringComparison.OrdinalIgnoreCase) ||
                HasNestedPath(obj, "media_inputs", "image", "start") ||
                HasNestedPath(obj, "media_inputs", "image", "reference");
            var excluded = IsImageOnlyModel(info);

            if (outputsVideo && inputsImage && imageToVideo && !excluded)
            {
                info.SupportsImageToVideo = true;
                info.SupportsStartImage = HasNestedPath(obj, "media_inputs", "image", "start") || json.Contains("start_image", StringComparison.OrdinalIgnoreCase);
                info.SupportsReferenceImage = HasNestedPath(obj, "media_inputs", "image", "reference") || json.Contains("reference_image", StringComparison.OrdinalIgnoreCase);
                models.Add(info);
            }
        }

        WriteVideoDiagnostics(modelsNode, models);
        return models;
    }

    public async Task<IReadOnlyList<WanGpModelInfo>> GetAvailableAudioModelsAsync(CancellationToken cancellationToken = default)
    {
        var allNode = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?>
        {
            ["include_availability"] = true
        }, cancellationToken);

        var audioNode = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?>
        {
            ["main_output"] = "audio",
            ["include_availability"] = true
        }, cancellationToken);

        var textAudioNode = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?>
        {
            ["main_output"] = "audio",
            ["inputs"] = "text",
            ["include_availability"] = true
        }, cancellationToken);

        var models = ExtractAudioModels(audioNode)
            .Concat(ExtractAudioModels(textAudioNode))
            .Concat(ExtractAudioModels(allNode).Where(model =>
                model.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase) ||
                model.DisplayName.Contains("kugel", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(model => model.ModelType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(model => !string.IsNullOrWhiteSpace(model.ModelType))
            .ToList();

        WriteAudioDiagnostics(allNode, models);
        return models;
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(tool => tool.Name).OrderBy(name => name).ToList();
    }

    public async Task<WanGpModelSchema?> GetModelSchemaAsync(string modelType, CancellationToken cancellationToken = default)
    {
        var schemaNode = await CallToolNodeAsync("wangp_get_model_schema", new Dictionary<string, object?>
        {
            ["model_type"] = modelType
        }, cancellationToken);

        var defaults = await CallToolNodeAsync("wangp_get_default_settings", new Dictionary<string, object?>
        {
            ["model_type"] = modelType
        }, cancellationToken);

        var schema = UnwrapObject(schemaNode, "result") ?? schemaNode as JsonObject ?? new JsonObject();
        var defaultSettings = UnwrapObject(defaults, "result") ?? defaults as JsonObject ?? new JsonObject();
        return new WanGpModelSchema
        {
            ModelType = modelType,
            RawSchema = schema,
            DefaultSettings = defaultSettings,
            SupportedResolutions = ExtractResolutions(schema),
            SupportsNegativePrompt = ContainsKey(schema, "negative_prompt", "negativePrompt"),
            SupportsSeed = ContainsKey(schema, "seed"),
            SupportsImageInput = ContainsKey(schema, "image", "input_image", "reference_image"),
            DefaultInferenceSteps = ReadInt(defaultSettings, 20, "num_inference_steps", "inference_steps", "steps")
        };
    }

    public async Task<WanGpGenerationSubmission> SubmitImageGenerationAsync(
        WanGpImageGenerationRequest request,
        WanGpModelSchema schema,
        CancellationToken cancellationToken = default)
    {
        var source = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["model_type"] = request.ModelType,
            ["prompt"] = request.Prompt,
            ["resolution"] = request.Resolution,
            ["num_inference_steps"] = request.InferenceSteps,
            ["image_mode"] = 1
        };

        if (schema.SupportsSeed && !request.RandomSeed && request.Seed is int seed)
        {
            source["seed"] = seed;
        }

        if (schema.SupportsNegativePrompt && !string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            source["negative_prompt"] = request.NegativePrompt;
        }

        foreach (var setting in schema.DefaultSettings)
        {
            source.TryAdd(setting.Key, setting.Value?.Deserialize<object>(JsonOptions));
        }

        var args = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["wait"] = false
        };

        var node = await CallToolNodeAsync("wangp_generate", args, cancellationToken);
        var obj = UnwrapObject(node, "result") ?? node as JsonObject ?? new JsonObject();
        return new WanGpGenerationSubmission
        {
            ExternalJobId = ReadString(obj, "job_id", "jobId", "id"),
            RawResponse = obj
        };
    }

    public async Task<WanGpGenerationSubmission> SubmitVideoGenerationAsync(
        IReadOnlyDictionary<string, object?> source,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["source"] = source,
            ["wait"] = false
        };

        var node = await CallToolNodeAsync("wangp_generate", args, cancellationToken);
        var obj = UnwrapObject(node, "result") ?? node as JsonObject ?? new JsonObject();
        return new WanGpGenerationSubmission
        {
            ExternalJobId = ReadString(obj, "job_id", "jobId", "id"),
            RawResponse = obj
        };
    }

    public Task<WanGpGenerationSubmission> SubmitAudioGenerationAsync(
        IReadOnlyDictionary<string, object?> source,
        CancellationToken cancellationToken = default)
    {
        return SubmitVideoGenerationAsync(source, cancellationToken);
    }

    public async Task<WanGpJobSnapshot> GetJobAsync(string externalJobId, CancellationToken cancellationToken = default)
    {
        var node = await CallToolNodeAsync("wangp_get_job", new Dictionary<string, object?>
        {
            ["job_id"] = externalJobId
        }, cancellationToken);

        var obj = UnwrapObject(node, "result") ?? node as JsonObject ?? new JsonObject();
        var result = UnwrapObject(obj, "result");
        var generatedFiles = ReadGeneratedFiles(result);
        var outputPath = generatedFiles.FirstOrDefault() ?? ReadString(obj, "output_path", "file_path", "path", "output");
        return new WanGpJobSnapshot
        {
            ExternalJobId = externalJobId,
            Status = MapSnapshotStatus(obj),
            ProgressPercentage = ReadSnapshotProgress(obj),
            Phase = ReadLatestEventValue(obj, "phase", "status") ?? ReadString(obj, "phase", "current_phase"),
            CurrentStep = ReadLatestEventInt(obj, "current_step", "step") ?? ReadNullableInt(obj, "current_step", "step"),
            TotalSteps = ReadLatestEventInt(obj, "total_steps", "steps") ?? ReadNullableInt(obj, "total_steps", "steps"),
            Message = ReadString(obj, "message", "detail"),
            OutputPath = outputPath,
            GeneratedFiles = generatedFiles,
            Seed = ReadNullableInt(obj, "seed")
        };
    }

    public async Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken = default)
    {
        await CallToolNodeAsync("wangp_cancel_job", new Dictionary<string, object?>
        {
            ["job_id"] = externalJobId
        }, cancellationToken);
    }

    private async Task<McpClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_options.Endpoint),
            Name = "WanGP",
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(15)
        }, _loggerFactory);

        return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
    }

    private async Task<JsonNode> CallToolNodeAsync(string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        await using var client = await CreateClientAsync(cancellationToken);
        var result = await client.CallToolAsync(toolName, args!, cancellationToken: cancellationToken);
        if (result.IsError == true)
        {
            throw new InvalidOperationException($"WanGP MCP tool failed: {toolName}");
        }

        if (result.StructuredContent is not null)
        {
            return JsonSerializer.SerializeToNode(result.StructuredContent, JsonOptions) ?? new JsonObject();
        }

        return JsonSerializer.SerializeToNode(result, JsonOptions) ?? new JsonObject();
    }

    private static List<string> ExtractResolutions(JsonObject schema)
    {
        var json = schema.ToJsonString();
        var common = new[] { "512x512", "768x768", "1024x1024", "1280x720", "1920x1080" };
        return common.Where(json.Contains).DefaultIfEmpty("1024x1024").Distinct().ToList();
    }

    private static JsonArray? ReadArray(JsonNode node, params string[] keys)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        if (node is not JsonObject obj)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var value) && value is JsonArray childArray)
            {
                return childArray;
            }
        }

        return null;
    }

    private static bool ContainsKey(JsonNode node, params string[] keys)
    {
        var json = node.ToJsonString();
        return keys.Any(key => json.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject? UnwrapObject(JsonNode node, string propertyName)
    {
        return node is JsonObject obj &&
            obj.TryGetPropertyValue(propertyName, out var value) &&
            value is JsonObject child
                ? child
                : null;
    }

    private static bool SupportsValue(JsonObject obj, string expected, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetPropertyValue(key, out var node) || node is null)
            {
                continue;
            }

            if (node is JsonArray array && array.Any(item => string.Equals(item?.ToString(), expected, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (string.Equals(node.ToString(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadString(JsonObject obj, params string[] keys)
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

    private static bool IsImageOnlyModel(WanGpModelInfo info)
    {
        var haystack = $"{info.ModelType} {info.DisplayName} {info.Family} {info.MainOutput} {info.Outputs}".ToLowerInvariant();
        return haystack.Contains("qwen_image") ||
            haystack.Contains("qwen image") ||
            haystack.Contains("krea") ||
            haystack.Contains("flux") ||
            haystack.Contains("z-image") ||
            (haystack.Contains("image") && !haystack.Contains("video") && !haystack.Contains("i2v"));
    }

    private static bool HasKeyValue(JsonNode? node, string key, string expected)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    pair.Value?.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }

                if (HasKeyValue(pair.Value, key, expected))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            return array.Any(item => HasKeyValue(item, key, expected) || item?.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase) == true);
        }

        return false;
    }

    private static bool HasNestedPath(JsonNode? node, params string[] path)
    {
        JsonNode? current = node;
        foreach (var segment in path)
        {
            if (current is not JsonObject obj)
            {
                return false;
            }

            var match = obj.FirstOrDefault(pair => string.Equals(pair.Key, segment, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key))
            {
                return false;
            }

            current = match.Value;
        }

        return current is not null && !string.Equals(current.ToString(), "false", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteVideoDiagnostics(JsonArray allModels, IReadOnlyList<WanGpModelInfo> filtered)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "DirectorWanGpVideoDiagnostics");
            Directory.CreateDirectory(root);
            var lines = new List<string>
            {
                $"CapturedAt={DateTime.Now:O}",
                $"RawVideoFilterCount={allModels.Count}",
                $"FilteredI2VCount={filtered.Count}"
            };
            foreach (var model in filtered)
            {
                lines.Add($"model_type={model.ModelType}; name={model.DisplayName}; family={model.Family}; architecture={model.Architecture}; main_output={model.MainOutput}; outputs={model.Outputs}; inputs={model.Inputs}; availability={model.Availability}; start={model.SupportsStartImage}; reference={model.SupportsReferenceImage}");
            }

            File.WriteAllLines(Path.Combine(root, $"video-models-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), lines);
        }
        catch
        {
            // Diagnostics must not affect model discovery.
        }
    }

    private static IEnumerable<WanGpModelInfo> ExtractAudioModels(JsonNode node)
    {
        var models = new List<WanGpModelInfo>();
        var modelsNode = ReadArray(node, "models", "result") ?? node as JsonArray ?? new JsonArray();
        foreach (var item in modelsNode)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var info = new WanGpModelInfo
            {
                ModelType = ReadString(obj, "model_type", "modelType", "type", "name"),
                DisplayName = ReadString(obj, "display_name", "displayName", "name", "model_type"),
                Availability = ReadString(obj, "availability", "status"),
                MainOutput = ReadString(obj, "main_output", "mainOutput", "outputs"),
                Family = ReadString(obj, "family"),
                Architecture = ReadString(obj, "architecture"),
                BaseModelType = ReadString(obj, "base_model_type", "baseModelType"),
                Outputs = ReadString(obj, "outputs"),
                Inputs = ReadString(obj, "inputs", "input_types", "media_inputs"),
                RawMetadata = obj.DeepClone() as JsonObject ?? new JsonObject()
            };

            var outputsAudio = SupportsValue(obj, "audio", "main_output", "mainOutput", "outputs") ||
                HasKeyValue(obj, "output", "audio") ||
                HasKeyValue(obj, "outputs", "audio");
            var inputsText = SupportsValue(obj, "text", "inputs", "input_types") ||
                HasKeyValue(obj, "input", "text") ||
                HasKeyValue(obj, "inputs", "text") ||
                obj.ToJsonString().Contains("prompt", StringComparison.OrdinalIgnoreCase);

            if (outputsAudio && inputsText)
            {
                models.Add(info);
            }
        }

        return models;
    }

    private static void WriteAudioDiagnostics(JsonNode allModels, IReadOnlyList<WanGpModelInfo> filtered)
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "DirectorWanGpAudioDiagnostics");
            Directory.CreateDirectory(root);
            WriteSanitizedAudioModels(Path.Combine(root, "all-audio-models.json"), filtered);
            WriteSanitizedAudioModels(
                Path.Combine(root, "kugelaudio-models.json"),
                filtered.Where(model =>
                    model.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase) ||
                    model.DisplayName.Contains("kugel", StringComparison.OrdinalIgnoreCase)).ToList());
        }
        catch
        {
            // Diagnostics must not affect discovery.
        }
    }

    private static void WriteSanitizedAudioModels(string path, IReadOnlyList<WanGpModelInfo> models)
    {
        var array = new JsonArray();
        foreach (var model in models)
        {
            array.Add(new JsonObject
            {
                ["model_type"] = model.ModelType,
                ["name"] = model.DisplayName,
                ["family"] = model.Family,
                ["architecture"] = model.Architecture,
                ["inputs"] = model.Inputs,
                ["outputs"] = model.Outputs,
                ["availability"] = model.Availability,
                ["capabilities"] = model.RawMetadata["capabilities"]?.DeepClone(),
                ["media_inputs"] = model.RawMetadata["media_inputs"]?.DeepClone(),
                ["schema_keys"] = new JsonArray(model.RawMetadata.Select(pair => JsonValue.Create(pair.Key)).ToArray())
            });
        }

        File.WriteAllText(path, array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int ReadInt(JsonObject obj, int fallback, params string[] keys)
    {
        return ReadNullableInt(obj, keys) ?? fallback;
    }

    private static int? ReadNullableInt(JsonObject obj, params string[] keys)
    {
        var value = ReadString(obj, keys);
        return int.TryParse(value, out var number) ? number : null;
    }

    private static double ReadDouble(JsonObject obj, double fallback, params string[] keys)
    {
        var value = ReadString(obj, keys);
        if (!double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return fallback;
        }

        return number <= 1 ? number * 100 : number;
    }

    private static GenerationJobStatus MapStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "pending" => GenerationJobStatus.Pending,
            "queued" => GenerationJobStatus.Queued,
            "running" or "processing" or "inference" => GenerationJobStatus.Running,
            "completed" or "succeeded" or "success" => GenerationJobStatus.Completed,
            "cancelled" or "canceled" => GenerationJobStatus.Cancelled,
            "interrupted" => GenerationJobStatus.Interrupted,
            "failed" or "error" => GenerationJobStatus.Failed,
            _ => GenerationJobStatus.Running
        };
    }

    private static GenerationJobStatus MapSnapshotStatus(JsonObject obj)
    {
        if (ReadBool(obj, "cancel_requested"))
        {
            return GenerationJobStatus.Cancelled;
        }

        if (ReadBool(obj, "done"))
        {
            var result = UnwrapObject(obj, "result");
            if (ReadBool(result, "cancelled"))
            {
                return GenerationJobStatus.Cancelled;
            }

            return ReadBool(result, "success") ? GenerationJobStatus.Completed : GenerationJobStatus.Failed;
        }

        return MapStatus(ReadString(obj, "status", "state"));
    }

    private static double ReadSnapshotProgress(JsonObject obj)
    {
        var latest = ReadLatestEventDouble(obj, "progress", "percentage");
        return latest ?? ReadDouble(obj, 0, "progress", "progress_percentage", "percentage");
    }

    private static string? ReadFirstGeneratedFile(JsonObject? result)
    {
        return ReadGeneratedFiles(result).FirstOrDefault();
    }

    private static List<string> ReadGeneratedFiles(JsonObject? result)
    {
        if (result is null)
        {
            return [];
        }

        var paths = new List<string>();
        if (result.TryGetPropertyValue("artifacts", out var artifactsNode) && artifactsNode is JsonArray artifacts)
        {
            paths.AddRange(artifacts
                .OfType<JsonObject>()
                .Where(artifact => IsGeneratedMediaArtifact(artifact))
                .Select(artifact => ReadString(artifact, "path", "file_path", "filePath", "output_path", "url"))
                .Where(IsSupportedGeneratedMediaPath));
        }

        if (result.TryGetPropertyValue("generated_files", out var filesNode) && filesNode is JsonArray files)
        {
            paths.AddRange(files
            .Select(file => file?.ToString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Where(IsSupportedGeneratedMediaPath));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsGeneratedMediaArtifact(JsonObject artifact)
    {
        var mediaType = ReadString(artifact, "media_type", "mediaType", "type", "mime_type", "mimeType");
        var path = ReadString(artifact, "path", "file_path", "filePath", "output_path", "url");
        return mediaType.Contains("video", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
            IsSupportedGeneratedMediaPath(path);
    }

    private static bool IsSupportedGeneratedMediaPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && GeneratedMediaExtensions.Contains(Path.GetExtension(path));
    }

    private static string? ReadLatestEventValue(JsonObject obj, params string[] keys)
    {
        var data = ReadLatestPreviewData(obj);
        return data is null ? null : ReadString(data, keys);
    }

    private static int? ReadLatestEventInt(JsonObject obj, params string[] keys)
    {
        var data = ReadLatestPreviewData(obj);
        return data is null ? null : ReadNullableInt(data, keys);
    }

    private static double? ReadLatestEventDouble(JsonObject obj, params string[] keys)
    {
        var data = ReadLatestPreviewData(obj);
        if (data is null)
        {
            return null;
        }

        return ReadDouble(data, 0, keys);
    }

    private static JsonObject? ReadLatestPreviewData(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("events", out var eventsNode) || eventsNode is not JsonArray events)
        {
            return null;
        }

        return events
            .OfType<JsonObject>()
            .Reverse()
            .Select(item => UnwrapObject(item, "data"))
            .FirstOrDefault(data => data is not null && ContainsKey(data, "progress", "phase", "status", "current_step", "total_steps"));
    }

    private static bool ReadBool(JsonObject? obj, params string[] keys)
    {
        if (obj is null)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && bool.TryParse(node?.ToString(), out var value))
            {
                return value;
            }
        }

        return false;
    }
}
