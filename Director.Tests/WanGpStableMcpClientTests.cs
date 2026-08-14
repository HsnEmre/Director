using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Director.Enums;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class WanGpStableMcpClientTests
{
    [Fact]
    public void CanonicalEndpoint_AddsTrailingSlash()
    {
        var endpoint = WanGpStableMcpClient.CanonicalizeEndpoint("http://127.0.0.1:7866/mcp");

        Assert.Equal("http://127.0.0.1:7866/mcp/", endpoint.ToString());
    }

    [Fact]
    public void WanGpEndpoint_DriftFromRuntimePort_IsRejected()
    {
        var result = new WanGpOptionsValidator().Validate(
            null,
            new WanGpOptions
            {
                Endpoint = "http://127.0.0.1:8000/mcp",
                Host = "127.0.0.1",
                Port = 7866
            });

        Assert.True(result.Failed);
        Assert.Contains("WanGp:Endpoint must match WanGp:Host and WanGp:Port", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeCoordinatorSidecarEndpoint_MatchesStableClientEndpoint()
    {
        var options = new WanGpOptions
        {
            Endpoint = "http://127.0.0.1:7866/mcp/",
            Host = "127.0.0.1",
            Port = 7866
        };
        var factory = new FakeSessionFactory(_ => new FakeSession());
        var client = CreateClient(factory, options);

        _ = await client.TestConnectionAsync();

        var launchedEndpoint = $"http://{options.Host}:{options.Port}/mcp/";
        Assert.Equal(launchedEndpoint, factory.Endpoints.Single().ToString());
        Assert.Contains("--mcp-host 127.0.0.1", WanGpRuntimeCoordinator.BuildMcpArguments(options), StringComparison.Ordinal);
        Assert.Contains("--mcp-port 7866", WanGpRuntimeCoordinator.BuildMcpArguments(options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeCoordinator_PortOpenWithTransientHandshakeFailure_RetriesBeforePortConflict()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var root = Path.Combine(Path.GetTempPath(), "DirectorWanGpRuntimeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "wgp.py"), "# test");
        try
        {
            var options = new WanGpOptions
            {
                Endpoint = $"http://127.0.0.1:{port}/mcp/",
                Host = "127.0.0.1",
                Port = port,
                RootPath = root,
                PythonExecutablePath = Environment.ProcessPath ?? typeof(object).Assembly.Location,
                AutoStart = false,
                McpHandshakeRetrySeconds = 2,
                McpHandshakeRetryIntervalMilliseconds = 100
            };
            var client = new FlakyHandshakeWanGpClient(failuresBeforeReady: 1);
            var coordinator = new WanGpRuntimeCoordinator(
                client,
                Microsoft.Extensions.Options.Options.Create(options),
                new ApplicationActivityCenter(),
                NullLogger<WanGpRuntimeCoordinator>.Instance);

            var status = await coordinator.EnsureReadyAsync();

            Assert.True(status.IsReady);
            Assert.Equal(WanGpMcpConnectionState.Connected, status.McpState);
            Assert.True(client.TestConnectionCallCount >= 2);
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentCalls_CreateSingleSessionAndSingleToolRefresh()
    {
        var factory = new FakeSessionFactory(_ => new FakeSession());
        var client = CreateClient(factory);

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.ListToolsAsync()));

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, client.SessionGenerationForTesting);
        Assert.Equal(1, client.ToolRefreshCountForTesting);
        Assert.Equal(1, factory.Sessions.Single().ListToolsCallCount);
    }

    [Fact]
    public async Task ListTools_UsesInitializedCacheOnly()
    {
        var session = new FakeSession();
        var client = CreateClient(new FakeSessionFactory(_ => session));

        _ = await client.ListToolsAsync();
        _ = await client.GetAvailableImageModelsAsync();

        Assert.Equal(1, session.ListToolsCallCount);
        Assert.Equal(1, client.ToolRefreshCountForTesting);
    }

    [Fact]
    public async Task MissingGenerateTool_ReturnsTypedContractErrorBeforeJobSubmit()
    {
        var session = new FakeSession(tools: RequiredExcept("wangp_generate"));
        var client = CreateClient(new FakeSessionFactory(_ => session));

        var exception = await Assert.ThrowsAsync<WanGpToolContractException>(() =>
            client.ListToolsAsync());

        Assert.Contains("wangp_generate", exception.Message);
        Assert.Equal(0, session.CallToolCount);
    }

    [Fact]
    public async Task TransportFailure_ReconnectsOnceAndSucceeds()
    {
        var factory = new FakeSessionFactory(index =>
        {
            var session = new FakeSession();
            if (index == 0)
            {
                session.ThrowOnToolCall = new IOException("stream closed");
            }

            return session;
        });
        var client = CreateClient(factory);

        var models = await client.GetAvailableImageModelsAsync();

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(2, client.SessionGenerationForTesting);
        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task TransportFailure_DoesNotReconnectForever()
    {
        var factory = new FakeSessionFactory(_ => new FakeSession { ThrowOnToolCall = new IOException("stream closed") });
        var client = CreateClient(factory);

        await Assert.ThrowsAsync<WanGpMcpTransportException>(() => client.GetAvailableImageModelsAsync());

        Assert.Equal(2, factory.CreateCount);
    }

    [Fact]
    public async Task GenerateAndGetJob_ReuseSameSession()
    {
        var factory = new FakeSessionFactory(_ => new FakeSession());
        var client = CreateClient(factory);

        var submit = await client.SubmitVideoGenerationAsync(new Dictionary<string, object?> { ["model_type"] = "ltx" });
        var job = await client.GetJobAsync(submit.ExternalJobId);

        Assert.Equal("job-1", submit.ExternalJobId);
        Assert.Equal(GenerationJobStatus.Completed, job.Status);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(2, factory.Sessions.Single().CallToolCount);
    }

    [Fact]
    public async Task EmptySubmitJobId_IsTypedSubmitFailure()
    {
        var session = new FakeSession
        {
            GenerateResponse = new JsonObject { ["status"] = "accepted" }
        };
        var client = CreateClient(new FakeSessionFactory(_ => session));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SubmitVideoGenerationAsync(new Dictionary<string, object?> { ["model_type"] = "ltx" }));

        Assert.Contains("job id", exception.Message);
    }

    [Fact]
    public async Task Dispose_DoesNotClassifyNormalCancellationAsFailure()
    {
        var session = new FakeSession { ThrowOnDispose = new OperationCanceledException("normal shutdown") };
        var client = CreateClient(new FakeSessionFactory(_ => session));

        _ = await client.ListToolsAsync();
        await client.DisposeAsync();

        Assert.Equal(1, session.DisposeCallCount);
    }

    private static WanGpStableMcpClient CreateClient(FakeSessionFactory factory) =>
        CreateClient(factory, new WanGpOptions { Endpoint = "http://127.0.0.1:7866/mcp/" });

    private static WanGpStableMcpClient CreateClient(FakeSessionFactory factory, WanGpOptions options) =>
        new(
            Microsoft.Extensions.Options.Options.Create(options),
            NullLoggerFactory.Instance,
            NullLogger<WanGpStableMcpClient>.Instance,
            factory.CreateAsync);

    private static IReadOnlyList<string> RequiredExcept(string missing) =>
        RequiredTools().Where(tool => !tool.Equals(missing, StringComparison.OrdinalIgnoreCase)).ToList();

    private static IReadOnlyList<string> RequiredTools() =>
    [
        "wangp_list_models",
        "wangp_get_model_schema",
        "wangp_get_default_settings",
        "wangp_generate",
        "wangp_get_job",
        "wangp_cancel_job"
    ];

    private sealed class FlakyHandshakeWanGpClient(int failuresBeforeReady) : IWanGpClient
    {
        public int TestConnectionCallCount { get; private set; }

        public Task<WanGpConnectionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            TestConnectionCallCount++;
            return Task.FromResult(TestConnectionCallCount <= failuresBeforeReady
                ? new WanGpConnectionResult { IsAvailable = false, Message = "transient handshake failure" }
                : new WanGpConnectionResult { IsAvailable = true, Message = "ready" });
        }

        public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(RequiredTools());

        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WanGpModelInfo>>([]);

        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableImageToVideoModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WanGpModelInfo>>([]);

        public Task<IReadOnlyList<WanGpModelInfo>> GetAvailableAudioModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WanGpModelInfo>>([]);

        public Task<WanGpModelSchema?> GetModelSchemaAsync(string modelType, CancellationToken cancellationToken = default) =>
            Task.FromResult<WanGpModelSchema?>(null);

        public Task<WanGpGenerationSubmission> SubmitImageGenerationAsync(
            WanGpImageGenerationRequest request,
            WanGpModelSchema schema,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WanGpGenerationSubmission> SubmitVideoGenerationAsync(
            IReadOnlyDictionary<string, object?> source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WanGpGenerationSubmission> SubmitAudioGenerationAsync(
            IReadOnlyDictionary<string, object?> source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WanGpJobSnapshot> GetJobAsync(string externalJobId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelJobAsync(string externalJobId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeSessionFactory(Func<int, FakeSession> create)
    {
        public int CreateCount { get; private set; }
        public List<FakeSession> Sessions { get; } = [];
        public List<Uri> Endpoints { get; } = [];

        public Task<IWanGpMcpSession> CreateAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/mcp/", endpoint.ToString());
            Endpoints.Add(endpoint);
            var session = create(CreateCount);
            CreateCount++;
            Sessions.Add(session);
            return Task.FromResult<IWanGpMcpSession>(session);
        }
    }

    private sealed class FakeSession(IReadOnlyList<string>? tools = null) : IWanGpMcpSession
    {
        public int ListToolsCallCount { get; private set; }
        public int CallToolCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public Exception? ThrowOnToolCall { get; set; }
        public Exception? ThrowOnDispose { get; set; }
        public JsonObject GenerateResponse { get; set; } = new()
        {
            ["job_id"] = "job-1",
            ["status"] = "accepted"
        };

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken cancellationToken)
        {
            ListToolsCallCount++;
            return Task.FromResult(tools ?? RequiredTools());
        }

        public Task<JsonNode> CallToolNodeAsync(string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
        {
            CallToolCount++;
            if (ThrowOnToolCall is not null)
            {
                throw ThrowOnToolCall;
            }

            JsonNode response = toolName switch
            {
                "wangp_list_models" => new JsonObject
                {
                    ["models"] = new JsonArray(new JsonObject
                    {
                        ["model_type"] = "ltx2_22B_distilled_gguf_q4_k_m",
                        ["display_name"] = "LTX",
                        ["main_output"] = "image",
                        ["availability"] = "installed"
                    })
                },
                "wangp_generate" => new JsonObject { ["result"] = GenerateResponse.DeepClone() },
                "wangp_get_job" => new JsonObject
                {
                    ["status"] = "completed",
                    ["done"] = true,
                    ["result"] = new JsonObject
                    {
                        ["success"] = true,
                        ["generated_files"] = new JsonArray("C:\\outputs\\video.mp4")
                    }
                },
                _ => new JsonObject()
            };
            return Task.FromResult(response);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            if (ThrowOnDispose is not null)
            {
                throw ThrowOnDispose;
            }

            return ValueTask.CompletedTask;
        }
    }
}
