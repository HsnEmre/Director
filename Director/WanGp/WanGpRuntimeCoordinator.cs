using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using Director.Enums;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.WanGp;

public sealed class WanGpRuntimeCoordinator : IWanGpRuntimeCoordinator
{
    private static readonly string[] RequiredTools =
    [
        "wangp_list_models",
        "wangp_get_model_schema",
        "wangp_get_default_settings",
        "wangp_generate",
        "wangp_get_job",
        "wangp_cancel_job"
    ];

    private readonly IWanGpClient _client;
    private readonly WanGpOptions _options;
    private readonly IApplicationActivityCenter _activity;
    private readonly ILogger<WanGpRuntimeCoordinator> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _ownedProcess;

    public WanGpRuntimeCoordinator(
        IWanGpClient client,
        IOptions<WanGpOptions> options,
        IApplicationActivityCenter activity,
        ILogger<WanGpRuntimeCoordinator> logger)
    {
        _client = client;
        _options = options.Value;
        _activity = activity;
        _logger = logger;
        LastStatus = new WanGpRuntimeStatus
        {
            GuiState = WanGpGuiState.Unknown,
            McpState = WanGpMcpConnectionState.Unknown,
            Message = "WanGP durumu henüz kontrol edilmedi."
        };
    }

    public WanGpRuntimeStatus LastStatus { get; private set; }

    public async Task<WanGpRuntimeStatus> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        var guiOpen = await IsPortOpenAsync(7860, cancellationToken);
        var connection = await _client.TestConnectionAsync(cancellationToken);
        var status = new WanGpRuntimeStatus
        {
            GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
            McpState = connection.IsAvailable ? WanGpMcpConnectionState.Connected : WanGpMcpConnectionState.Disconnected,
            IsReady = connection.IsAvailable,
            IsOwnedProcess = _ownedProcess is { HasExited: false },
            ProcessId = _ownedProcess is { HasExited: false } ? _ownedProcess.Id : null,
            Message = connection.IsAvailable
                ? "MCP bağlantısı kuruldu."
                : guiOpen
                    ? "WanGP web arayüzü çalışıyor ancak Yönetmen entegrasyonu için gereken MCP sunucusu bağlı değil."
                    : "WanGP MCP bağlantısı yok."
        };

        if (connection.IsAvailable)
        {
            status.Tools = await _client.ListToolsAsync(cancellationToken);
        }

        SetStatus(status);
        return status;
    }

    public async Task<WanGpRuntimeStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _activity.AddLog("WanGP", "WanGP kurulumu doğrulanıyor.");
            if (!ValidateConfiguration(out var validationMessage))
            {
                return SetStatus(new WanGpRuntimeStatus
                {
                    GuiState = await IsPortOpenAsync(7860, cancellationToken) ? WanGpGuiState.Open : WanGpGuiState.Closed,
                    McpState = WanGpMcpConnectionState.InvalidConfiguration,
                    Message = validationMessage
                });
            }

            _activity.AddLog("WanGP", "Python ortamı doğrulanıyor.");
            var existing = await ValidateHandshakeAsync(cancellationToken);
            if (existing.IsReady)
            {
                return SetStatus(existing);
            }

            var guiOpen = await IsPortOpenAsync(7860, cancellationToken);
            var mcpPortOpen = await IsPortOpenAsync(_options.Port, cancellationToken);
            if (mcpPortOpen)
            {
                return SetStatus(new WanGpRuntimeStatus
                {
                    GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
                    McpState = WanGpMcpConnectionState.PortConflict,
                    Message = "7866 portu açık fakat MCP handshake başarısız. Port çakışması olabilir."
                });
            }

            if (!_options.AutoStart)
            {
                return SetStatus(new WanGpRuntimeStatus
                {
                    GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
                    McpState = WanGpMcpConnectionState.Disconnected,
                    Message = "MCP sunucusu kapalı ve AutoStart devre dışı."
                });
            }

            _activity.AddLog("WanGP", "MCP sidecar başlatılıyor.");
            StartOwnedProcess();
            var deadline = DateTime.UtcNow.AddSeconds(_options.StartupTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _activity.AddLog("WanGP", "Port bekleniyor.");
                if (await IsPortOpenAsync(_options.Port, cancellationToken))
                {
                    _activity.AddLog("WanGP", "MCP bağlantısı kuruluyor.");
                    var ready = await ValidateHandshakeAsync(cancellationToken);
                    if (ready.IsReady)
                    {
                        return SetStatus(ready);
                    }
                }

                await Task.Delay(1000, cancellationToken);
            }

            return SetStatus(new WanGpRuntimeStatus
            {
                GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
                McpState = WanGpMcpConnectionState.Disconnected,
                IsOwnedProcess = _ownedProcess is { HasExited: false },
                ProcessId = _ownedProcess is { HasExited: false } ? _ownedProcess.Id : null,
                Message = "MCP sunucusu startup timeout süresinde hazır olmadı."
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StopOwnedProcessAsync(CancellationToken cancellationToken = default)
    {
        if (_ownedProcess is { HasExited: false })
        {
            _activity.AddLog("WanGP", "Yönetmen tarafından başlatılan MCP sidecar kapatılıyor.", GenerationLogLevel.Warning);
            _ownedProcess.Kill(entireProcessTree: true);
        }

        return Task.CompletedTask;
    }

    private async Task<WanGpRuntimeStatus> ValidateHandshakeAsync(CancellationToken cancellationToken)
    {
        var guiOpen = await IsPortOpenAsync(7860, cancellationToken);
        var connection = await _client.TestConnectionAsync(cancellationToken);
        if (!connection.IsAvailable)
        {
            return new WanGpRuntimeStatus
            {
                GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
                McpState = WanGpMcpConnectionState.Disconnected,
                Message = connection.Message
            };
        }

        _activity.AddLog("WanGP", "Araçlar doğrulanıyor.");
        var tools = await _client.ListToolsAsync(cancellationToken);
        var missing = RequiredTools.Except(tools, StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            return new WanGpRuntimeStatus
            {
                GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
                McpState = WanGpMcpConnectionState.InvalidConfiguration,
                Message = "MCP araçları eksik: " + string.Join(", ", missing),
                Tools = tools
            };
        }

        return new WanGpRuntimeStatus
        {
            GuiState = guiOpen ? WanGpGuiState.Open : WanGpGuiState.Closed,
            McpState = WanGpMcpConnectionState.Connected,
            IsReady = true,
            IsOwnedProcess = _ownedProcess is { HasExited: false },
            ProcessId = _ownedProcess is { HasExited: false } ? _ownedProcess.Id : null,
            Message = "MCP bağlantısı kuruldu.",
            Tools = tools
        };
    }

    private void StartOwnedProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.PythonExecutablePath,
            Arguments = $"wgp.py --mcp --mcp-transport streamable-http --mcp-host {_options.Host} --mcp-port {_options.Port}",
            WorkingDirectory = _options.RootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["FASTMCP_HOST"] = _options.Host;
        startInfo.Environment["FASTMCP_PORT"] = _options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _ownedProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("WanGP MCP process başlatılamadı.");
        _ownedProcess.OutputDataReceived += (_, e) => LogProcessLine("WanGP stdout", e.Data, GenerationLogLevel.Information);
        _ownedProcess.ErrorDataReceived += (_, e) => LogProcessLine("WanGP stderr", e.Data, GenerationLogLevel.Warning);
        _ownedProcess.BeginOutputReadLine();
        _ownedProcess.BeginErrorReadLine();
        _activity.AddLog("WanGP", $"MCP sidecar başlatıldı. PID: {_ownedProcess.Id}");
    }

    private void LogProcessLine(string phase, string? line, GenerationLogLevel level)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _logger.LogInformation("{Phase}: {Line}", phase, line);
        if (line.Contains("prompt", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("base64", StringComparison.OrdinalIgnoreCase) ||
            line.Length > 500)
        {
            return;
        }

        _activity.AddLog(phase, line, level);
    }

    private WanGpRuntimeStatus SetStatus(WanGpRuntimeStatus status)
    {
        LastStatus = status;
        _activity.SetRuntimeStatus(status);
        return status;
    }

    private bool ValidateConfiguration(out string message)
    {
        if (!Directory.Exists(_options.RootPath))
        {
            message = "WanGP RootPath bulunamadı.";
            return false;
        }

        if (!File.Exists(Path.Combine(_options.RootPath, "wgp.py")))
        {
            message = "WanGP RootPath altında wgp.py bulunamadı.";
            return false;
        }

        if (!File.Exists(_options.PythonExecutablePath))
        {
            message = "WanGP Python executable bulunamadı.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
