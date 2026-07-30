using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class OllamaModelLifecycleService : IOllamaModelLifecycleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;

    public OllamaModelLifecycleService(HttpClient httpClient, IOptions<OllamaOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(Math.Max(1, _options.RequestTimeoutMinutes));
    }

    public async Task<IReadOnlyList<string>> GetRunningModelsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("/api/ps", cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var payload = JsonSerializer.Deserialize<OllamaProcessResponse>(body, JsonOptions);
        return payload?.Models.Select(model => model.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()
            ?? [];
    }

    public async Task UnloadModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var request = new
        {
            model = modelName,
            stream = false,
            keep_alive = 0,
            messages = Array.Empty<object>()
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/chat", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> WaitUntilUnloadedAsync(string modelName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.Now.Add(timeout);
        while (DateTime.Now < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var running = await GetRunningModelsAsync(cancellationToken);
            if (!running.Any(model => string.Equals(model, modelName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    private sealed class OllamaProcessResponse
    {
        public List<OllamaProcessModel> Models { get; set; } = [];
    }

    private sealed class OllamaProcessModel
    {
        public string Name { get; set; } = string.Empty;
    }
}
