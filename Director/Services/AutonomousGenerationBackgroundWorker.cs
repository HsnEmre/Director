using System.Collections.Concurrent;
using Director.Options;
using Director.Services.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Director.Services;

public sealed class AutonomousGenerationBackgroundWorker : BackgroundService
{
    private readonly IAutonomousGenerationRunService _runService;
    private readonly IAutonomousGenerationOrchestrator _orchestrator;
    private readonly ILogger<AutonomousGenerationBackgroundWorker> _logger;
    private readonly AutonomousGenerationOptions _options;
    private readonly ConcurrentDictionary<int, byte> _activeRunIds = new();
    private readonly string _workerId = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public AutonomousGenerationBackgroundWorker(
        IAutonomousGenerationRunService runService,
        IAutonomousGenerationOrchestrator orchestrator,
        ILogger<AutonomousGenerationBackgroundWorker> logger,
        IOptions<AutonomousGenerationOptions> options)
    {
        _runService = runService;
        _orchestrator = orchestrator;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var runnableRuns = await _runService.GetRunnableRunsAsync(stoppingToken);
                foreach (var run in runnableRuns)
                {
                    if (!_activeRunIds.TryAdd(run.Id, 0))
                    {
                        continue;
                    }

                    var claimed = await _runService.TryClaimRunAsync(
                        run.Id,
                        _workerId,
                        _options.StaleHeartbeatThreshold,
                        _options.LeaseExtension,
                        stoppingToken);
                    if (!claimed)
                    {
                        _activeRunIds.TryRemove(run.Id, out _);
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _orchestrator.RunAsync(run.Id, _workerId, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            // App shutdown; run remains resumable by heartbeat/checkpoint.
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Autonomous background run {RunId} crashed outside orchestrator.", run.Id);
                        }
                        finally
                        {
                            _activeRunIds.TryRemove(run.Id, out _);
                        }
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Autonomous background worker poll failed. If the migration is not applied yet this is expected until DB update.");
            }

            try
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
