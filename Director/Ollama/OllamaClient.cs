using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Director.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Ollama;

public sealed class OllamaClient : IOllamaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
    private static readonly OllamaStructuredJsonParser StructuredJsonParser = new();

    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(HttpClient httpClient, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Ollama connection check. BaseUrl={BaseUrl}", _options.BaseUrl);
            using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new OllamaHealthResult
                {
                    IsAvailable = false,
                    Message = $"Ollama servisi hata dondurdu: {(int)response.StatusCode} {response.ReasonPhrase}"
                };
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return new OllamaHealthResult { IsAvailable = false, Message = "Ollama model listesi bos dondu." };
            }

            var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(body, JsonOptions);
            var models = tags?.Models?
                .Select(model => model.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList() ?? [];

            return new OllamaHealthResult
            {
                IsAvailable = true,
                Message = "Ollama servisine ulasildi.",
                Models = models
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Ollama connection check cancelled.");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Ollama service unavailable.");
            return new OllamaHealthResult
            {
                IsAvailable = false,
                Message = "Ollama servisine ulasilamadi. Ollama'nin calistigindan emin olun."
            };
        }
    }

    public async Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var health = await CheckHealthAsync(cancellationToken);
        if (!health.IsAvailable)
        {
            throw new InvalidOperationException(health.Message);
        }

        if (!health.Models.Any(model => string.Equals(model, modelName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"{modelName} modeli Ollama icinde bulunamadi.");
        }

        return true;
    }

    public async Task<TResponse> ChatStructuredAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages,
        object jsonSchema,
        string? modelOverride = null,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default,
        IProgress<OllamaStreamProgress>? streamProgress = null,
        OllamaGenerationSettings? generationSettings = null)
    {
        var result = await ChatStructuredDetailedAsync<TResponse>(
            messages,
            jsonSchema,
            modelOverride,
            requestTimeout,
            cancellationToken,
            streamProgress,
            generationSettings);
        return result.Value;
    }

    public async Task<OllamaStructuredResult<TResponse>> ChatStructuredDetailedAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages,
        object jsonSchema,
        string? modelOverride = null,
        TimeSpan? requestTimeout = null,
        CancellationToken cancellationToken = default,
        IProgress<OllamaStreamProgress>? streamProgress = null,
        OllamaGenerationSettings? generationSettings = null)
    {
        var model = string.IsNullOrWhiteSpace(modelOverride) ? _options.Model : modelOverride;
        var effectiveNumPredict = generationSettings?.NumPredict ?? _options.SceneNumPredict;
        var request = new
        {
            model,
            stream = true,
            think = generationSettings?.Think ?? false,
            keep_alive = _options.KeepAlive,
            messages = messages.Select(message => new { role = message.Role, content = message.Content, images = message.Images }),
            format = jsonSchema,
            options = new
            {
                temperature = generationSettings?.Temperature ?? _options.Temperature,
                top_p = generationSettings?.TopP ?? _options.TopP,
                top_k = generationSettings?.TopK,
                repeat_penalty = generationSettings?.RepeatPenalty,
                repeat_last_n = generationSettings?.RepeatLastN,
                num_ctx = _options.ContextLength,
                num_predict = effectiveNumPredict
            }
        };

        var stopwatch = Stopwatch.StartNew();
        var hardTimeout = requestTimeout ?? TimeSpan.FromMinutes(Math.Max(1, _options.SceneHardTimeoutMinutes));
        var firstTokenTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.SceneFirstTokenTimeoutSeconds));
        var noActivityTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.SceneNoActivityTimeoutSeconds));
        var responseCharacterLimit = Math.Max(1, _options.MaxStructuredResponseCharacters);
        var contentBuilder = new StringBuilder();
        var repetitionGuard = new OllamaRepetitionGuard(_options);
        var contentChunkCount = 0;
        var lastActivity = DateTimeOffset.UtcNow;
        var firstContentReceived = false;
        OllamaChatStreamChunk? finalChunk = null;

        ReportStream(streamProgress, OllamaStreamStage.RequestStarted, model, stopwatch.Elapsed, lastActivity, contentChunkCount);
        ReportStream(streamProgress, OllamaStreamStage.ModelPreparing, model, stopwatch.Elapsed, lastActivity, contentChunkCount);

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };
        using var response = await SendForStreamingAsync(requestMessage, firstTokenTimeout, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning("Ollama HTTP request failed. Model={Model}; Status={StatusCode}; ErrorLength={ErrorLength}", model, (int)response.StatusCode, errorBody.Length);
            throw new OllamaHttpResponseException(
                $"Ollama HTTP istegi basarisiz oldu: {(int)response.StatusCode} {response.ReasonPhrase}",
                errorBody,
                new OllamaResponseMetadata
                {
                    Model = model,
                    Endpoint = "/api/chat",
                    OperationName = generationSettings?.OperationName ?? string.Empty,
                    FilmProjectId = generationSettings?.FilmProjectId,
                    SceneNumber = generationSettings?.SceneNumber,
                    ConfiguredResponseLimit = responseCharacterLimit,
                    ResponseCharacterCount = errorBody.Length
                });
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream);

        try
        {
            while (true)
            {
                var timeout = firstContentReceived ? noActivityTimeout : firstTokenTimeout;
                string? line;
                try
                {
                    line = await ReadLineWithTimeoutAsync(
                        reader,
                        timeout,
                        cancellationToken,
                        () => ReportStream(
                            streamProgress,
                            OllamaStreamStage.ActivityHeartbeat,
                            model,
                            stopwatch.Elapsed,
                            lastActivity,
                            contentChunkCount));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var timeoutKind = firstContentReceived ? "token activity" : "first token";
                    throw new TimeoutException($"Ollama {timeoutKind} timeout. Model={model}; Seconds={timeout.TotalSeconds:0}.");
                }

                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                OllamaChatStreamChunk chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<OllamaChatStreamChunk>(line, JsonOptions)
                        ?? throw new JsonException("Empty NDJSON envelope.");
                }
                catch (JsonException ex)
                {
                    var invalidEnvelopeMetadata = BuildMetadata(model, stopwatch.Elapsed, contentBuilder, contentChunkCount, finalChunk, generationSettings, responseCharacterLimit);
                    throw new OllamaIncompleteStreamException("Ollama NDJSON stream paketi okunamadi.", contentBuilder.ToString(), invalidEnvelopeMetadata, ex);
                }

                // Only final content is assembled. message.thinking is intentionally ignored.
                var contentPart = chunk.Message?.Content;
                if (!string.IsNullOrEmpty(contentPart))
                {
                    if (contentBuilder.Length + contentPart.Length > responseCharacterLimit)
                    {
                        var tooLargeMetadata = BuildMetadata(model, stopwatch.Elapsed, contentBuilder, contentChunkCount, finalChunk, generationSettings, responseCharacterLimit);
                        tooLargeMetadata.ResponseCharacterCount = contentBuilder.Length + contentPart.Length;
                        var operation = string.IsNullOrWhiteSpace(tooLargeMetadata.OperationName) ? "StructuredChat" : tooLargeMetadata.OperationName;
                        throw new OllamaResponseTooLargeException(
                            $"Ollama structured response limit exceeded. Model={model}; Operation={operation}; ConfiguredLimit={responseCharacterLimit}; ReceivedCharacters={tooLargeMetadata.ResponseCharacterCount}; FilmProjectId={tooLargeMetadata.FilmProjectId?.ToString() ?? "(none)"}; SceneNumber={tooLargeMetadata.SceneNumber?.ToString() ?? "(none)"}.",
                            contentBuilder.ToString(),
                            tooLargeMetadata);
                    }

                    contentBuilder.Append(contentPart);
                    contentChunkCount++;
                    lastActivity = DateTimeOffset.UtcNow;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (repetitionGuard.TryDetect(contentBuilder, out var repetition))
                    {
                        var repetitionMetadata = BuildMetadata(model, stopwatch.Elapsed, contentBuilder, contentChunkCount, finalChunk, generationSettings, responseCharacterLimit);
                        repetitionMetadata.RepeatedBlockLength = repetition.BlockLength;
                        repetitionMetadata.RepeatedBlockCount = repetition.RepeatCount;
                        repetitionMetadata.RepeatedBlockPreview = repetition.Preview;
                        throw new OllamaRepetitionDetectedException(
                            $"Ollama response repetition detected. Model={model}; BlockLength={repetition.BlockLength}; RepeatCount={repetition.RepeatCount}.",
                            contentBuilder.ToString(),
                            repetitionMetadata);
                    }

                    var stage = firstContentReceived ? OllamaStreamStage.ContentChunk : OllamaStreamStage.FirstContentChunk;
                    firstContentReceived = true;
                    ReportStream(streamProgress, stage, model, stopwatch.Elapsed, lastActivity, contentChunkCount);
                }

                if (chunk.Done)
                {
                    finalChunk = chunk;
                    break;
                }

                if (stopwatch.Elapsed > hardTimeout &&
                    OllamaActivityTimeoutPolicy.HasTimedOut(DateTimeOffset.UtcNow, lastActivity, noActivityTimeout))
                {
                    throw new TimeoutException($"Ollama scene hard timeout. Model={model}; Elapsed={stopwatch.Elapsed:hh\\:mm\\:ss}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException ex)
        {
            var disconnectedMetadata = BuildMetadata(model, stopwatch.Elapsed, contentBuilder, contentChunkCount, finalChunk, generationSettings, responseCharacterLimit);
            throw new OllamaIncompleteStreamException("Ollama stream baglantisi tamamlanmadan kesildi.", contentBuilder.ToString(), disconnectedMetadata, ex);
        }

        stopwatch.Stop();
        var metadata = BuildMetadata(model, stopwatch.Elapsed, contentBuilder, contentChunkCount, finalChunk, generationSettings, responseCharacterLimit);
        if (string.IsNullOrWhiteSpace(metadata.DoneReason) && metadata.ResponseTokenCount >= effectiveNumPredict)
        {
            metadata.DoneReason = "token_limit_inferred";
        }
        if (finalChunk is null || !finalChunk.Done)
        {
            throw new OllamaIncompleteStreamException(
                "Ollama stream done=true paketi alinmadan sona erdi.",
                contentBuilder.ToString(),
                metadata);
        }

        ReportStream(streamProgress, OllamaStreamStage.Completed, model, stopwatch.Elapsed, lastActivity, contentChunkCount, finalChunk, contentBuilder.Length);
        _logger.LogInformation(
            "Ollama streaming completed. Model={Model}; Done={Done}; DoneReason={DoneReason}; ElapsedMs={ElapsedMs}; Chunks={ChunkCount}; ContentLength={ContentLength}; PromptTokens={PromptTokenCount}; ResponseTokens={ResponseTokenCount}; LoadMs={LoadMs}; EvalMs={EvalMs}",
            model,
            metadata.Done,
            metadata.DoneReason,
            stopwatch.ElapsedMilliseconds,
            contentChunkCount,
            contentBuilder.Length,
            finalChunk?.PromptEvalCount ?? 0,
            finalChunk?.EvalCount ?? 0,
            NanosecondsToTimeSpan(finalChunk?.LoadDuration ?? 0).TotalMilliseconds,
            NanosecondsToTimeSpan(finalChunk?.EvalDuration ?? 0).TotalMilliseconds);

        if (IsTokenLimitReason(metadata.DoneReason))
        {
            throw new OllamaResponseTruncatedException(
                $"Ollama yaniti token sinirinda kesildi. DoneReason={metadata.DoneReason}.",
                contentBuilder.ToString(),
                metadata);
        }

        ReportStream(streamProgress, OllamaStreamStage.JsonValidating, model, stopwatch.Elapsed, lastActivity, contentChunkCount, finalChunk);
        try
        {
            return StructuredJsonParser.Parse<TResponse>(contentBuilder.ToString(), metadata);
        }
        catch (OllamaStructuredResponseException ex)
        {
            _logger.LogWarning(
                "Structured model response failed. Model={Model}; Stage={Stage}; ContentLength={ContentLength}; Path={JsonPath}; Line={Line}; Byte={Byte}",
                model,
                ex.Stage,
                contentBuilder.Length,
                ex.JsonPath,
                ex.LineNumber,
                ex.BytePositionInLine);
            throw;
        }
    }

    private static OllamaResponseMetadata BuildMetadata(
        string model,
        TimeSpan elapsed,
        StringBuilder content,
        int contentChunkCount,
        OllamaChatStreamChunk? finalChunk,
        OllamaGenerationSettings? generationSettings,
        int configuredResponseLimit) =>
        new()
        {
            Model = model,
            OperationName = generationSettings?.OperationName ?? string.Empty,
            OutputProfile = generationSettings?.OutputProfile,
            PromptCharacterCount = generationSettings?.PromptCharacterCount,
            EstimatedPromptTokens = generationSettings?.EstimatedPromptTokens,
            FilmProjectId = generationSettings?.FilmProjectId,
            SceneNumber = generationSettings?.SceneNumber,
            ConfiguredResponseLimit = configuredResponseLimit,
            StreamCompleted = finalChunk?.Done == true,
            Done = finalChunk?.Done == true,
            DoneReason = finalChunk?.DoneReason ?? string.Empty,
            PromptTokenCount = finalChunk?.PromptEvalCount ?? 0,
            ResponseTokenCount = finalChunk?.EvalCount ?? 0,
            ContentChunkCount = contentChunkCount,
            ResponseCharacterCount = content.Length,
            Elapsed = elapsed,
            LoadDuration = NanosecondsToTimeSpan(finalChunk?.LoadDuration ?? 0),
            EvaluationDuration = NanosecondsToTimeSpan(finalChunk?.EvalDuration ?? 0)
        };

    private static bool IsTokenLimitReason(string reason) =>
        reason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        reason.Contains("limit", StringComparison.OrdinalIgnoreCase);

    private async Task<HttpResponseMessage> SendForStreamingAsync(
        HttpRequestMessage request,
        TimeSpan firstTokenTimeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(firstTokenTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Ollama connection/model preparation timeout. Seconds={firstTokenTimeout.TotalSeconds:0}.");
        }
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(
        StreamReader reader,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action heartbeat)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var readTask = reader.ReadLineAsync(linkedCts.Token).AsTask();
        while (!readTask.IsCompleted)
        {
            var heartbeatTask = Task.Delay(TimeSpan.FromSeconds(5), linkedCts.Token);
            var completed = await Task.WhenAny(readTask, heartbeatTask);
            if (completed == readTask)
            {
                break;
            }

            heartbeat();
        }

        return await readTask;
    }

    private static void ReportStream(
        IProgress<OllamaStreamProgress>? progress,
        OllamaStreamStage stage,
        string model,
        TimeSpan elapsed,
        DateTimeOffset lastActivity,
        int contentChunkCount,
        OllamaChatStreamChunk? finalChunk = null,
        int responseCharacterCount = 0)
    {
        progress?.Report(new OllamaStreamProgress
        {
            Stage = stage,
            Model = model,
            Elapsed = elapsed,
            TimeSinceLastActivity = DateTimeOffset.UtcNow - lastActivity,
            ContentChunkCount = contentChunkCount,
            PromptTokenCount = finalChunk?.PromptEvalCount ?? 0,
            ResponseTokenCount = finalChunk?.EvalCount ?? 0,
            Done = finalChunk?.Done == true,
            DoneReason = finalChunk?.DoneReason ?? string.Empty,
            ResponseCharacterCount = responseCharacterCount,
            LoadDuration = NanosecondsToTimeSpan(finalChunk?.LoadDuration ?? 0),
            EvaluationDuration = NanosecondsToTimeSpan(finalChunk?.EvalDuration ?? 0)
        });
    }

    private static TimeSpan NanosecondsToTimeSpan(long nanoseconds) =>
        nanoseconds <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(nanoseconds / 100);

    private sealed class OllamaTagsResponse
    {
        public List<OllamaTagModel> Models { get; set; } = [];
    }

    private sealed class OllamaTagModel
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class OllamaChatStreamChunk
    {
        public OllamaChatResponseMessage? Message { get; set; }
        public bool Done { get; set; }

        [JsonPropertyName("done_reason")]
        public string DoneReason { get; set; } = string.Empty;

        [JsonPropertyName("load_duration")]
        public long LoadDuration { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public int PromptEvalCount { get; set; }

        [JsonPropertyName("eval_count")]
        public int EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }
    }

    private sealed class OllamaChatResponseMessage
    {
        public string Content { get; set; } = string.Empty;
    }
}
