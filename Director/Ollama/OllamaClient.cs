using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
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

    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaClient> _logger;

    public OllamaClient(HttpClient httpClient, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(Math.Max(1, _options.RequestTimeoutMinutes));
    }

    public async Task<OllamaHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Ollama bağlantısı kontrol ediliyor: {BaseUrl}", _options.BaseUrl);
            using var response = await _httpClient.GetAsync("/api/tags", cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new OllamaHealthResult
                {
                    IsAvailable = false,
                    Message = $"Ollama servisi hata döndürdü: {(int)response.StatusCode} {body}"
                };
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return new OllamaHealthResult
                {
                    IsAvailable = false,
                    Message = "Ollama model listesi boş döndü."
                };
            }

            var tags = JsonSerializer.Deserialize<OllamaTagsResponse>(body, JsonOptions);
            var models = tags?.Models?.Select(model => model.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()
                ?? new List<string>();

            return new OllamaHealthResult
            {
                IsAvailable = true,
                Message = "Ollama servisine ulaşıldı.",
                Models = models
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Ollama bağlantı kontrolü iptal edildi.");
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Ollama servisine ulaşılamadı.");
            return new OllamaHealthResult
            {
                IsAvailable = false,
                Message = "Ollama servisine ulaşılamadı. Ollama'nın çalıştığından emin olun."
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

        var isAvailable = health.Models.Any(model => string.Equals(model, modelName, StringComparison.OrdinalIgnoreCase));
        if (!isAvailable)
        {
            throw new InvalidOperationException($"{modelName} modeli Ollama içinde bulunamadı.");
        }

        return true;
    }

    public async Task<TResponse> ChatStructuredAsync<TResponse>(
        IReadOnlyList<OllamaChatMessage> messages,
        object jsonSchema,
        CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model = _options.Model,
            stream = false,
            keep_alive = _options.KeepAlive,
            messages = messages.Select(message => new { role = message.Role, content = message.Content, images = message.Images }),
            format = jsonSchema,
            options = new
            {
                temperature = _options.Temperature,
                top_p = _options.TopP,
                num_ctx = _options.ContextLength
            }
        };

        var stopwatch = Stopwatch.StartNew();
        using var response = await _httpClient.PostAsJsonAsync("/api/chat", request, JsonOptions, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();

        _logger.LogInformation("Ollama /api/chat yanıtı alındı. Model: {Model}, Süre: {ElapsedMs} ms", _options.Model, stopwatch.ElapsedMilliseconds);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama isteği başarısız oldu: {(int)response.StatusCode} {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Ollama yanıt gövdesi boş döndü.");
        }

        OllamaChatResponse? chatResponse;
        try
        {
            chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Ollama chat envelope parse edilemedi. Raw response: {RawResponse}", body);
            throw new InvalidOperationException("Model yanıtı beklenen JSON formatına dönüştürülemedi.", ex);
        }

        var content = CleanJsonContent(chatResponse?.Message?.Content);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Ollama model yanıtı boş döndü.");
        }

        try
        {
            var result = JsonSerializer.Deserialize<TResponse>(content, JsonOptions);
            return result ?? throw new InvalidOperationException("Model yanıtı beklenen JSON formatına dönüştürülemedi.");
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Structured model JSON parse edilemedi. Raw response: {RawResponse}", content);
            throw new InvalidOperationException("Model yanıtı beklenen JSON formatına dönüştürülemedi.", ex);
        }
    }

    private static string CleanJsonContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var trimmed = content.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].Trim();
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[3..].Trim();
        }

        if (trimmed.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].Trim();
        }

        return trimmed;
    }

    private sealed class OllamaTagsResponse
    {
        public List<OllamaTagModel> Models { get; set; } = new();
    }

    private sealed class OllamaTagModel
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class OllamaChatResponse
    {
        public OllamaChatResponseMessage? Message { get; set; }
    }

    private sealed class OllamaChatResponseMessage
    {
        public string Content { get; set; } = string.Empty;
    }
}
