using System.Diagnostics;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.WanGp;

public sealed class WanGpProcessManager : IWanGpProcessManager
{
    private readonly IWanGpClient _client;
    private readonly WanGpOptions _options;
    private readonly ILogger<WanGpProcessManager> _logger;
    private Process? _ownedProcess;

    public WanGpProcessManager(IWanGpClient client, IOptions<WanGpOptions> options, ILogger<WanGpProcessManager> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> EnsureServerAsync(IProgress<GenerationLogEntry>? logs = null, CancellationToken cancellationToken = default)
    {
        var existing = await _client.TestConnectionAsync(cancellationToken);
        if (existing.IsAvailable)
        {
            logs?.Report(Log("WanGP", "Mevcut WanGP MCP sunucusu bulundu.", GenerationLogLevel.Success));
            return true;
        }

        if (!_options.AutoStart)
        {
            logs?.Report(Log("WanGP", "WanGP bağlantısı kurulamadı. AutoStart kapalı.", GenerationLogLevel.Warning));
            return false;
        }

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

        _ownedProcess = Process.Start(startInfo);
        if (_ownedProcess is null)
        {
            return false;
        }

        _ownedProcess.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogInformation("WanGP stdout: {Line}", e.Data);
            }
        };
        _ownedProcess.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogWarning("WanGP stderr: {Line}", e.Data);
            }
        };
        _ownedProcess.BeginOutputReadLine();
        _ownedProcess.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(_options.StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _client.TestConnectionAsync(cancellationToken);
            if (result.IsAvailable)
            {
                logs?.Report(Log("WanGP", "WanGP MCP sunucusu başlatıldı.", GenerationLogLevel.Success));
                return true;
            }

            await Task.Delay(1000, cancellationToken);
        }

        return false;
    }

    public Task StopOwnedProcessAsync(CancellationToken cancellationToken = default)
    {
        if (_ownedProcess is { HasExited: false })
        {
            _ownedProcess.Kill(entireProcessTree: true);
        }

        return Task.CompletedTask;
    }

    private static GenerationLogEntry Log(string phase, string message, GenerationLogLevel level)
    {
        return new GenerationLogEntry
        {
            Timestamp = DateTime.Now,
            Phase = phase,
            Message = message,
            Level = level
        };
    }
}
