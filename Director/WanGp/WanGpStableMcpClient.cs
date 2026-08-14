using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Director.Enums;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Director.WanGp;

public sealed class WanGpStableMcpClient : IWanGpClient, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] RequiredTools =
    [
        "wangp_list_models",
        "wangp_get_model_schema",
        "wangp_get_default_settings",
        "wangp_generate",
        "wangp_get_job",
        "wangp_cancel_job"
    ];

    private static readonly HashSet<string> GeneratedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mov", ".mkv", ".wav", ".flac", ".mp3", ".ogg", ".m4a"
    };

    private readonly WanGpOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WanGpStableMcpClient> _logger;
    private readonly Func<Uri, CancellationToken, Task<IWanGpMcpSession>> _sessionFactory;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private WanGpMcpSessionState? _session;
    private int _sessionGeneration;
    private int _toolRefreshCount;

    public WanGpStableMcpClient(
        IOptions<WanGpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<WanGpStableMcpClient> logger)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _sessionFactory = CreateRealSessionAsync;
    }

    internal WanGpStableMcpClient(
        IOptions<WanGpOptions> options,
        ILoggerFactory loggerFactory,
        ILogger<WanGpStableMcpClient> logger,
        Func<Uri, CancellationToken, Task<IWanGpMcpSession>> sessionFactory)
        : this(options, loggerFactory, logger)
    {
        _sessionFactory = sessionFactory;
    }

    internal int SessionGenerationForTesting => _sessionGeneration;
    internal int ToolRefreshCountForTesting => _toolRefreshCount;

    public async Task<WanGpConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var session = await GetOrCreateSessionAsync(cancellationToken);
            await session.Client.PingAsync(cancellationToken);
            return new WanGpConnectionResult { IsAvailable = true, Message = "WanGP MCP baglantisi ve tool contract dogrulamasi basarili." };
        }
        catch (WanGpToolContractException ex)
        {
            _logger.LogWarning(ex, "WanGP MCP tool contract eksik.");
            return new WanGpConnectionResult { IsAvailable = false, Message = ex.Message };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WanGP MCP baglantisi kurulamadi.");
            return new WanGpConnectionResult { IsAvailable = false, Message = "WanGP baglantisi kurulamadi." };
        }
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(cancellationToken);
        return session.Tools.OrderBy(name => name).ToList();
    }

    public async Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageModelsAsync(CancellationToken cancellationToken = default)
    {
        var node = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?> { ["main_output"] = "image" }, cancellationToken);
        return ExtractModels(node)
            .Where(model => SupportsModel(model.RawMetadata, "image", "main_output", "mainOutput") || string.IsNullOrWhiteSpace(model.MainOutput))
            .ToList();
    }

    public async Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageToVideoModelsAsync(CancellationToken cancellationToken = default)
    {
        var node = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?>
        {
            ["main_output"] = "video",
            ["inputs"] = "image",
            ["include_availability"] = true
        }, cancellationToken);

        return ExtractModels(node)
            .Where(model => SupportsVideo(model) && SupportsImageInput(model) && !IsImageOnlyModel(model))
            .Select(model =>
            {
                model.SupportsImageToVideo = true;
                model.SupportsStartImage = ContainsText(model.RawMetadata, "start_image") || HasNestedPath(model.RawMetadata, "media_inputs", "image", "start");
                model.SupportsReferenceImage = ContainsText(model.RawMetadata, "reference_image") || HasNestedPath(model.RawMetadata, "media_inputs", "image", "reference");
                return model;
            })
            .ToList();
    }

    public async Task<IReadOnlyList<WanGpModelInfo>> GetAvailableAudioModelsAsync(CancellationToken cancellationToken = default)
    {
        var all = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?> { ["include_availability"] = true }, cancellationToken);
        var audio = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?> { ["main_output"] = "audio", ["include_availability"] = true }, cancellationToken);
        var textAudio = await CallToolNodeAsync("wangp_list_models", new Dictionary<string, object?> { ["main_output"] = "audio", ["inputs"] = "text", ["include_availability"] = true }, cancellationToken);
        return ExtractModels(audio)
            .Concat(ExtractModels(textAudio))
            .Concat(ExtractModels(all).Where(model =>
                model.ModelType.Contains("kugel", StringComparison.OrdinalIgnoreCase) ||
                model.DisplayName.Contains("kugel", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(model => model.ModelType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Where(model => !string.IsNullOrWhiteSpace(model.ModelType))
            .ToList();
    }

    public async Task<WanGpModelSchema?> GetModelSchemaAsync(string modelType, CancellationToken cancellationToken = default)
    {
        var schemaNode = await CallToolNodeAsync("wangp_get_model_schema", new Dictionary<string, object?> { ["model_type"] = modelType }, cancellationToken);
        var defaultsNode = await CallToolNodeAsync("wangp_get_default_settings", new Dictionary<string, object?> { ["model_type"] = modelType }, cancellationToken);
        var schema = UnwrapObject(schemaNode, "result") ?? schemaNode as JsonObject ?? new JsonObject();
        var defaults = UnwrapObject(defaultsNode, "result") ?? defaultsNode as JsonObject ?? new JsonObject();
        return new WanGpModelSchema
        {
            ModelType = modelType,
            RawSchema = schema,
            DefaultSettings = defaults,
            SupportedResolutions = ExtractResolutions(schema),
            SupportsNegativePrompt = ContainsKey(schema, "negative_prompt", "negativePrompt"),
            SupportsSeed = ContainsKey(schema, "seed"),
            SupportsImageInput = ContainsKey(schema, "image", "input_image", "reference_image"),
            DefaultInferenceSteps = ReadInt(defaults, 20, "num_inference_steps", "inference_steps", "steps")
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

        AddImageReferenceIfSupported(source, schema, request);

        foreach (var setting in schema.DefaultSettings)
        {
            source.TryAdd(setting.Key, setting.Value?.Deserialize<object>(JsonOptions));
        }

        return await SubmitGenerationAsync(source, "image", cancellationToken);
    }

    private static void AddImageReferenceIfSupported(
        Dictionary<string, object?> source,
        WanGpModelSchema schema,
        WanGpImageGenerationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceImagePath))
        {
            return;
        }

        var imageKey = ResolveImageReferenceKey(schema);
        if (string.IsNullOrWhiteSpace(imageKey))
        {
            throw new InvalidOperationException(
                $"Selected image model does not expose a reference/source image input in its WanGP schema. Model={request.ModelType}; sourceAssetId={request.SourceImageAssetId}.");
        }

        source[imageKey] = Path.GetFullPath(request.SourceImagePath);
    }

    private static string ResolveImageReferenceKey(WanGpModelSchema schema)
    {
        var schemaKey = WanGpVideoInputContractResolver.FindPropertyName(
            schema.RawSchema,
            "image_reference",
            "reference_image",
            "ref_image",
            "source_image",
            "input_image",
            "init_image",
            "image");
        if (!string.IsNullOrWhiteSpace(schemaKey))
        {
            return schemaKey;
        }

        return schema.DefaultSettings.Select(pair => pair.Key).FirstOrDefault(key =>
            key.Equals("image_reference", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("reference_image", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ref_image", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("source_image", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("input_image", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("init_image", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("image", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    public Task<WanGpGenerationSubmission> SubmitVideoGenerationAsync(
        IReadOnlyDictionary<string, object?> source,
        CancellationToken cancellationToken = default) =>
        SubmitGenerationAsync(source, "video", cancellationToken);

    public Task<WanGpGenerationSubmission> SubmitAudioGenerationAsync(
        IReadOnlyDictionary<string, object?> source,
        CancellationToken cancellationToken = default) =>
        SubmitGenerationAsync(source, "audio", cancellationToken);

    public async Task<WanGpJobSnapshot> GetJobAsync(string externalJobId, CancellationToken cancellationToken = default)
    {
        var node = await CallToolNodeAsync("wangp_get_job", new Dictionary<string, object?> { ["job_id"] = externalJobId }, cancellationToken);
        var root = node as JsonObject ?? new JsonObject();
        var obj = ContainsKey(root, "status", "state", "done")
            ? root
            : UnwrapObject(root, "result") ?? root;
        var result = UnwrapObject(obj, "result");
        var generatedFiles = ReadGeneratedFiles(result);
        var outputPath = generatedFiles.FirstOrDefault() ?? ReadString(obj, "output_path", "file_path", "path", "output");
        var completedAt = ReadDateTime(obj, "completed_at", "completedAt") ?? ReadDateTime(result, "completed_at", "completedAt");
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
            Seed = ReadNullableInt(obj, "seed"),
            CompletedAt = completedAt
        };
    }

    public async Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken = default)
    {
        await CallToolNodeAsync("wangp_cancel_job", new Dictionary<string, object?> { ["job_id"] = externalJobId }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessionGate.WaitAsync();
        try
        {
            if (_session is not null)
            {
                try
                {
                    await _session.Client.DisposeAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WanGP MCP session dispose during application shutdown.");
                }

                _session = null;
            }
        }
        finally
        {
            _sessionGate.Release();
            _sessionGate.Dispose();
        }
    }

    private async Task<WanGpGenerationSubmission> SubmitGenerationAsync(
        IReadOnlyDictionary<string, object?> source,
        string mediaKind,
        CancellationToken cancellationToken)
    {
        var node = await CallToolNodeAsync("wangp_generate", new Dictionary<string, object?>
        {
            ["source"] = source,
            ["wait"] = false
        }, cancellationToken);
        var obj = UnwrapObject(node, "result") ?? node as JsonObject ?? new JsonObject();
        return new WanGpGenerationSubmission
        {
            ExternalJobId = ReadString(obj, "job_id", "jobId", "id"),
            Status = ReadString(obj, "status", "state"),
            RawResponse = obj
        }.Validate(mediaKind);
    }

    private async Task<JsonNode> CallToolNodeAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var session = await GetOrCreateSessionAsync(cancellationToken);
            if (!session.Tools.Contains(toolName))
            {
                throw new WanGpToolContractException($"WanGP MCP tool contract eksik: {toolName}");
            }

            try
            {
                return await session.Client.CallToolNodeAsync(toolName, args, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRecoverableTransportFailure(ex) && attempt == 1)
            {
                _logger.LogWarning(ex, "WanGP MCP transport hatasi; session tek kez yenilenecek. Tool={ToolName}; Generation={Generation}", toolName, session.Generation);
                await ResetSessionAsync(session, CancellationToken.None);
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsRecoverableTransportFailure(ex))
            {
                throw new WanGpMcpTransportException($"WanGP MCP transport hatasi kalici hale geldi: {toolName}", ex);
            }
        }

        throw new WanGpMcpTransportException($"WanGP MCP transport hatasi kalici hale geldi: {toolName}", new InvalidOperationException(toolName));
    }

    private async Task<WanGpMcpSessionState> GetOrCreateSessionAsync(CancellationToken cancellationToken)
    {
        var existing = Volatile.Read(ref _session);
        if (existing is not null)
        {
            return existing;
        }

        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            existing = _session;
            if (existing is not null)
            {
                return existing;
            }

            var endpoint = CanonicalizeEndpoint(_options.GetEffectiveMcpEndpointText());
            var client = await _sessionFactory(endpoint, cancellationToken);
            try
            {
                var tools = await client.ListToolsAsync(cancellationToken);
                Interlocked.Increment(ref _toolRefreshCount);
                ValidateRequiredTools(tools);
                var state = new WanGpMcpSessionState(
                    client,
                    tools.ToHashSet(StringComparer.OrdinalIgnoreCase),
                    Interlocked.Increment(ref _sessionGeneration));
                Volatile.Write(ref _session, state);
                _logger.LogInformation("WanGP MCP session hazir. Endpoint={Endpoint}; Generation={Generation}; ToolCount={ToolCount}", endpoint, state.Generation, state.Tools.Count);
                return state;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task ResetSessionAsync(WanGpMcpSessionState expected, CancellationToken cancellationToken)
    {
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            if (!ReferenceEquals(_session, expected))
            {
                return;
            }

            _session = null;
            try
            {
                await expected.Client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WanGP MCP session dispose sirasinda beklenen cancellation/transport kapanisi yasanmis olabilir.");
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task<IWanGpMcpSession> CreateRealSessionAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = "WanGP",
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(15)
        }, _loggerFactory);

        var client = await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: cancellationToken);
        return new RealWanGpMcpSession(client);
    }

    internal static Uri CanonicalizeEndpoint(string endpoint)
    {
        var uri = new Uri(endpoint);
        if (uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath + "/"
        };
        return builder.Uri;
    }

    private static void ValidateRequiredTools(IReadOnlyList<string> tools)
    {
        var available = tools.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredTools.Where(tool => !available.Contains(tool)).ToList();
        if (missing.Count > 0)
        {
            throw new WanGpToolContractException("WanGP MCP araclаri eksik: " + string.Join(", ", missing));
        }
    }

    private static bool IsRecoverableTransportFailure(Exception exception) =>
        exception is HttpRequestException or IOException or TimeoutException or WanGpMcpTransportException ||
        exception.GetType().Name.Contains("Transport", StringComparison.OrdinalIgnoreCase) ||
        (exception.GetType().Name.Contains("Mcp", StringComparison.OrdinalIgnoreCase) &&
            exception.Message.Contains("connection", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<WanGpModelInfo> ExtractModels(JsonNode node)
    {
        var modelsNode = ReadArray(node, "models", "result") ?? node as JsonArray ?? new JsonArray();
        var models = new List<WanGpModelInfo>();
        foreach (var item in modelsNode.OfType<JsonObject>())
        {
            models.Add(new WanGpModelInfo
            {
                ModelType = ReadString(item, "model_type", "modelType", "type", "name"),
                DisplayName = ReadString(item, "display_name", "displayName", "name", "model_type"),
                Availability = ReadString(item, "availability", "status"),
                MainOutput = ReadString(item, "main_output", "mainOutput", "outputs"),
                Family = ReadString(item, "family"),
                Architecture = ReadString(item, "architecture"),
                BaseModelType = ReadString(item, "base_model_type", "baseModelType"),
                Outputs = ReadString(item, "outputs"),
                Inputs = ReadString(item, "inputs", "input_types", "media_inputs"),
                RawMetadata = item.DeepClone() as JsonObject ?? new JsonObject()
            });
        }

        return models;
    }

    private static bool SupportsVideo(WanGpModelInfo model) =>
        SupportsModel(model.RawMetadata, "video", "main_output", "mainOutput", "outputs") ||
        ContainsKeyValue(model.RawMetadata, "output", "video") ||
        ContainsKeyValue(model.RawMetadata, "outputs", "video");

    private static bool SupportsImageInput(WanGpModelInfo model) =>
        SupportsModel(model.RawMetadata, "image", "inputs", "input_types") ||
        ContainsKeyValue(model.RawMetadata, "input", "image") ||
        ContainsKeyValue(model.RawMetadata, "inputs", "image") ||
        HasNestedPath(model.RawMetadata, "media_inputs", "image", "start") ||
        HasNestedPath(model.RawMetadata, "media_inputs", "image", "reference");

    private static bool SupportsModel(JsonObject obj, string expected, params string[] keys)
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

            if (node.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static JsonObject? UnwrapObject(JsonNode? node, string propertyName) =>
        node is JsonObject obj &&
        obj.TryGetPropertyValue(propertyName, out var value) &&
        value is JsonObject child
            ? child
            : null;

    private static bool ContainsKey(JsonNode? node, params string[] keys)
    {
        var json = node?.ToJsonString() ?? string.Empty;
        return keys.Any(key => json.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsText(JsonNode? node, string value) =>
        node?.ToJsonString().Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsKeyValue(JsonNode? node, string key, string expected)
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

                if (ContainsKeyValue(pair.Value, key, expected))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            return array.Any(item => ContainsKeyValue(item, key, expected) || item?.ToString().Contains(expected, StringComparison.OrdinalIgnoreCase) == true);
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

    private static string ReadString(JsonObject? obj, params string[] keys)
    {
        if (obj is null)
        {
            return string.Empty;
        }

        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var node) && node is not null)
            {
                return node.ToString();
            }
        }

        return string.Empty;
    }

    private static int ReadInt(JsonObject obj, int fallback, params string[] keys) =>
        ReadNullableInt(obj, keys) ?? fallback;

    private static int? ReadNullableInt(JsonObject? obj, params string[] keys)
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

    private static DateTime? ReadDateTime(JsonObject? obj, params string[] keys)
    {
        var value = ReadString(obj, keys);
        return DateTime.TryParse(value, out var parsed) ? parsed : null;
    }

    private static GenerationJobStatus MapStatus(string status) =>
        status.ToLowerInvariant() switch
        {
            "pending" => GenerationJobStatus.Pending,
            "queued" => GenerationJobStatus.Queued,
            "accepted" => GenerationJobStatus.Queued,
            "running" or "processing" or "inference" => GenerationJobStatus.Running,
            "completed" or "succeeded" or "success" => GenerationJobStatus.Completed,
            "cancelled" or "canceled" => GenerationJobStatus.Cancelled,
            "interrupted" => GenerationJobStatus.Interrupted,
            "failed" or "error" or "rejected" => GenerationJobStatus.Failed,
            _ => GenerationJobStatus.Running
        };

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
                .Where(IsGeneratedMediaArtifact)
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

    private static bool IsSupportedGeneratedMediaPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && GeneratedMediaExtensions.Contains(Path.GetExtension(path));

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
        return data is null ? null : ReadDouble(data, 0, keys);
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

    private sealed record WanGpMcpSessionState(IWanGpMcpSession Client, HashSet<string> Tools, int Generation);

    private sealed class RealWanGpMcpSession(McpClient client) : IWanGpMcpSession
    {
        public async Task PingAsync(CancellationToken cancellationToken) =>
            await client.PingAsync(cancellationToken: cancellationToken);

        public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken)
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            return tools.Select(tool => tool.Name).OrderBy(name => name).ToList();
        }

        public async Task<JsonNode> CallToolNodeAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> args,
            CancellationToken cancellationToken)
        {
            var result = await client.CallToolAsync(toolName, args!, cancellationToken: cancellationToken);
            if (result.IsError == true)
            {
                throw new WanGpToolExecutionException(toolName, FormatToolError(result));
            }

            if (result.StructuredContent is not null)
            {
                return JsonSerializer.SerializeToNode(result.StructuredContent, JsonOptions) ?? new JsonObject();
            }

            return JsonSerializer.SerializeToNode(result, JsonOptions) ?? new JsonObject();
        }

        public ValueTask DisposeAsync() => client.DisposeAsync();

        private static string FormatToolError(object result)
        {
            try
            {
                var node = JsonSerializer.SerializeToNode(result, JsonOptions);
                var detail = node?.ToJsonString(JsonOptions) ?? string.Empty;
                if (detail.Length > 2000)
                {
                    detail = detail[..2000] + "...";
                }

                return detail;
            }
            catch
            {
                return result.ToString() ?? string.Empty;
            }
        }
    }
}

internal interface IWanGpMcpSession : IAsyncDisposable
{
    Task PingAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken);
    Task<JsonNode> CallToolNodeAsync(string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken);
}

internal static class WanGpGenerationSubmissionValidation
{
    public static WanGpGenerationSubmission Validate(this WanGpGenerationSubmission submission, string mediaKind)
    {
        if (string.IsNullOrWhiteSpace(submission.ExternalJobId))
        {
            throw new InvalidOperationException($"WanGP {mediaKind} job id dondurmedi.");
        }

        if (submission.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            submission.Status.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            submission.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"WanGP {mediaKind} job submit basarisiz: {submission.Status}");
        }

        return submission;
    }
}
