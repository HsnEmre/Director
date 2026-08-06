using System.Net;
using System.Text;
using Director.Ollama;
using Director.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class OllamaStreamingTests
{
    [Fact]
    public async Task StreamingResponse_TracksChunksAndMetrics()
    {
        var ndjson = string.Join('\n',
            "{\"message\":{\"content\":\"{\\\"value\\\":\"},\"done\":false}",
            "{\"message\":{\"content\":\"\\\"ok\\\"}\"},\"done\":false}",
            "{\"message\":{\"content\":\"\"},\"done\":true,\"load_duration\":1500000000,\"prompt_eval_count\":321,\"eval_count\":42,\"eval_duration\":2500000000}") + "\n";
        var handler = new RecordingHandler(ndjson);
        var options = Microsoft.Extensions.Options.Options.Create(new OllamaOptions());
        var client = new OllamaClient(new HttpClient(handler), options, NullLogger<OllamaClient>.Instance);
        var events = new ProgressCollector<OllamaStreamProgress>();

        var result = await client.ChatStructuredAsync<TestResponse>(
            [new OllamaChatMessage("user", "test")],
            new { type = "object" },
            options.Value.SceneTextModel,
            cancellationToken: CancellationToken.None,
            streamProgress: events);

        Assert.Equal("ok", result.Value);
        Assert.Contains("\"stream\":true", handler.RequestBody);
        Assert.Contains("\"think\":false", handler.RequestBody);
        Assert.Contains("\"keep_alive\":\"30m\"", handler.RequestBody);
        Assert.Contains(OllamaOptions.DefaultTextModel, handler.RequestBody);
        Assert.Contains(events.Items, item => item.Stage == OllamaStreamStage.FirstContentChunk);
        Assert.Contains(events.Items, item => item.Stage == OllamaStreamStage.ContentChunk);
        var completed = Assert.Single(events.Items, item => item.Stage == OllamaStreamStage.Completed);
        Assert.Equal(2, completed.ContentChunkCount);
        Assert.Equal(321, completed.PromptTokenCount);
        Assert.Equal(42, completed.ResponseTokenCount);
        Assert.Equal(TimeSpan.FromSeconds(1.5), completed.LoadDuration);
        Assert.True(completed.Done);
        Assert.Equal(14, completed.ResponseCharacterCount);
    }

    [Fact]
    public async Task StreamEndingWithoutDone_IsClassifiedAsIncompleteTransport()
    {
        const string ndjson = "{\"message\":{\"content\":\"{\\\"value\\\":\\\"partial\\\"}\"},\"done\":false}\n";
        var client = CreateClient(ndjson);

        var exception = await Assert.ThrowsAsync<OllamaIncompleteStreamException>(() =>
            client.ChatStructuredAsync<TestResponse>(
                [new OllamaChatMessage("user", "test")],
                new { type = "object" },
                cancellationToken: CancellationToken.None));

        Assert.False(exception.Metadata.Done);
        Assert.Equal("StreamCompletion", exception.Stage);
    }

    [Fact]
    public async Task DoneReasonLength_IsClassifiedAsTruncatedBeforeJsonParsing()
    {
        const string ndjson = "{\"message\":{\"content\":\"{\\\"value\\\":\\\"partial\"},\"done\":true,\"done_reason\":\"length\",\"prompt_eval_count\":1200,\"eval_count\":4096}\n";
        var client = CreateClient(ndjson);

        var exception = await Assert.ThrowsAsync<OllamaResponseTruncatedException>(() =>
            client.ChatStructuredAsync<TestResponse>(
                [new OllamaChatMessage("user", "test")],
                new { type = "object" },
                cancellationToken: CancellationToken.None));

        Assert.True(exception.Metadata.Done);
        Assert.Equal("length", exception.Metadata.DoneReason);
        Assert.Equal(4096, exception.Metadata.ResponseTokenCount);
        Assert.Equal("TokenLimit", exception.Stage);
    }

    [Fact]
    public async Task ImageNegativePromptRepetition_IsDetectedBeforeTokenLimit()
    {
        var block = ", 3D animasyonlu tasarimli, 3D animasyonlu boyutlu, 3D animasyonlu cizimli ";
        var raw = "{\"imageNegativePrompt\":\"" + string.Concat(Enumerable.Repeat(block, 4));
        var ndjson = "{\"message\":{\"content\":" + JsonString(raw) + "},\"done\":false}\n" +
                     "{\"message\":{\"content\":\"tail\"},\"done\":true,\"done_reason\":\"length\",\"eval_count\":6144}\n";
        var client = CreateClient(ndjson, RepetitionOptions());

        var exception = await Assert.ThrowsAsync<OllamaRepetitionDetectedException>(() =>
            client.ChatStructuredAsync<TestResponse>(
                [new OllamaChatMessage("user", "test")],
                new { type = "object" },
                cancellationToken: CancellationToken.None));

        Assert.Equal("RepetitionDetected", exception.Stage);
        Assert.True(exception.Metadata.RepeatedBlockLength >= 48);
        Assert.True(exception.Metadata.RepeatedBlockCount >= 4);
        Assert.NotEqual(6144, exception.Metadata.ResponseTokenCount);
    }

    [Fact]
    public async Task VideoNegativePromptRepetition_IsDetected()
    {
        var block = ", face morphing background warping sudden camera jump duplicated limbs ";
        var raw = "{\"videoNegativePrompt\":\"" + string.Concat(Enumerable.Repeat(block, 4));
        var ndjson = "{\"message\":{\"content\":" + JsonString(raw) + "},\"done\":false}\n";
        var client = CreateClient(ndjson, RepetitionOptions());

        var exception = await Assert.ThrowsAsync<OllamaRepetitionDetectedException>(() =>
            client.ChatStructuredAsync<TestResponse>(
                [new OllamaChatMessage("user", "test")],
                new { type = "object" },
                cancellationToken: CancellationToken.None));

        Assert.Contains("face morphing", exception.Metadata.RepeatedBlockPreview);
    }

    [Fact]
    public async Task NormalShortRepeat_DoesNotTriggerRepetitionGuard()
    {
        const string ndjson = "{\"message\":{\"content\":\"{\\\"value\\\":\\\"go go go, look look\\\"}\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        var client = CreateClient(ndjson, RepetitionOptions());

        var result = await client.ChatStructuredAsync<TestResponse>(
            [new OllamaChatMessage("user", "test")],
            new { type = "object" },
            cancellationToken: CancellationToken.None);

        Assert.Equal("go go go, look look", result.Value);
    }

    [Fact]
    public async Task ThinkingChannel_IsNotAppendedToFinalJson()
    {
        const string ndjson = "{\"message\":{\"thinking\":\"not json { reasoning }\",\"content\":\"{\\\"value\\\":\\\"ok\\\"}\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        var client = CreateClient(ndjson);

        var result = await client.ChatStructuredAsync<TestResponse>(
            [new OllamaChatMessage("user", "test")],
            new { type = "object" },
            cancellationToken: CancellationToken.None);

        Assert.Equal("ok", result.Value);
    }

    [Fact]
    public async Task ResponseLimit_AllowsExactConfiguredLength()
    {
        const string ndjson = "{\"message\":{\"content\":\"{\\\"value\\\":\\\"ok\\\"}\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        var client = CreateClient(ndjson, new OllamaOptions { MaxStructuredResponseCharacters = 14 });

        var result = await client.ChatStructuredAsync<TestResponse>(
            [new OllamaChatMessage("user", "test")],
            new { type = "object" },
            cancellationToken: CancellationToken.None);

        Assert.Equal("ok", result.Value);
    }

    [Fact]
    public async Task ResponseLimit_ThrowsBeforeUnboundedAssembly()
    {
        const string ndjson = "{\"message\":{\"content\":\"{\\\"value\\\":\"},\"done\":false}\n{\"message\":{\"content\":\"\\\"ok\\\"}\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\"}\n";
        var client = CreateClient(ndjson, new OllamaOptions { MaxStructuredResponseCharacters = 13 });

        var exception = await Assert.ThrowsAsync<OllamaResponseTooLargeException>(() =>
            client.ChatStructuredAsync<TestResponse>(
                [new OllamaChatMessage("user", "test")],
                new { type = "object" },
                cancellationToken: CancellationToken.None,
                generationSettings: new OllamaGenerationSettings
                {
                    OperationName = "SingleSceneGeneration",
                    FilmProjectId = 9,
                    SceneNumber = 1
                }));

        Assert.Equal("ResponseTooLarge", exception.Stage);
        Assert.Equal(13, exception.Metadata.ConfiguredResponseLimit);
        Assert.Equal(14, exception.Metadata.ResponseCharacterCount);
        Assert.Equal("SingleSceneGeneration", exception.Metadata.OperationName);
        Assert.Equal(9, exception.Metadata.FilmProjectId);
        Assert.Equal(1, exception.Metadata.SceneNumber);
        Assert.Equal("{\"value\":", exception.ResponseContent);
    }

    [Fact]
    public void ActivityTimeout_DoesNotTriggerAfterRecentToken()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(OllamaActivityTimeoutPolicy.HasTimedOut(now, now.AddSeconds(-10), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ActivityTimeout_TriggersAfterTokenActivityStops()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(OllamaActivityTimeoutPolicy.HasTimedOut(now, now.AddSeconds(-31), TimeSpan.FromSeconds(30)));
    }

    private sealed class TestResponse
    {
        public string Value { get; set; } = string.Empty;
    }

    private static OllamaClient CreateClient(string responseBody) =>
        CreateClient(responseBody, new OllamaOptions());

    private static OllamaClient CreateClient(string responseBody, OllamaOptions options) =>
        new(
            new HttpClient(new RecordingHandler(responseBody)),
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<OllamaClient>.Instance);

    private static OllamaOptions RepetitionOptions() => new()
    {
        RepetitionGuardMinCharacters = 128,
        RepetitionGuardMinBlockCharacters = 48,
        RepetitionGuardMaxBlockCharacters = 160,
        RepetitionGuardMinConsecutiveRepeats = 4
    };

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private sealed class ProgressCollector<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }

    private sealed class RecordingHandler(string responseBody) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/x-ndjson")
            };
        }
    }
}
