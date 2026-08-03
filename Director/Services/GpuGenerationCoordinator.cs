using System.Diagnostics;
using System.IO;
using Director.Enums;
using Director.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class GpuGenerationCoordinator : IGpuGenerationCoordinator
{
    public const string LockNamespace = "Director.GpuLease.v1";
    public const string WaitingMessage = "GPU başka bir Director işlemi tarafından kullanılıyor. Sıra bekleniyor.";
    private static readonly TimeSpan PollingInterval = TimeSpan.FromMilliseconds(100);
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ILogger<GpuGenerationCoordinator> _logger;
    private readonly IApplicationActivityCenter? _activityCenter;
    private readonly string _lockPath;
    private int _isBusy;

    public GpuGenerationCoordinator(ILogger<GpuGenerationCoordinator> logger)
        : this(logger, null, ProjectGenerationLeaseCoordinator.GetDefaultLockDirectory(), LockNamespace)
    {
    }

    public GpuGenerationCoordinator(
        ILogger<GpuGenerationCoordinator> logger,
        IApplicationActivityCenter activityCenter)
        : this(logger, activityCenter, ProjectGenerationLeaseCoordinator.GetDefaultLockDirectory(), LockNamespace)
    {
    }

    public GpuGenerationCoordinator(
        ILogger<GpuGenerationCoordinator> logger,
        string lockDirectory,
        string lockNamespace)
        : this(logger, null, lockDirectory, lockNamespace)
    {
    }

    private GpuGenerationCoordinator(
        ILogger<GpuGenerationCoordinator> logger,
        IApplicationActivityCenter? activityCenter,
        string lockDirectory,
        string lockNamespace)
    {
        _logger = logger;
        _activityCenter = activityCenter;
        _lockPath = Path.Combine(lockDirectory, lockNamespace + ".lock");
    }

    public bool IsBusy => Volatile.Read(ref _isBusy) == 1;

    public async Task<IAsyncDisposable> AcquireAsync(
        GenerationOperationType operationType,
        int projectId,
        int sceneId,
        CancellationToken cancellationToken = default)
    {
        var waitingReported = 0;
        void ReportWaiting()
        {
            if (Interlocked.Exchange(ref waitingReported, 1) != 0)
            {
                return;
            }

            _logger.LogInformation("{Message} Operation={OperationType}; ProjectId={ProjectId}; SceneId={SceneId}", WaitingMessage, operationType, projectId, sceneId);
            _activityCenter?.AddLog("GPU sırası", WaitingMessage, GenerationLogLevel.Information);
        }

        if (!await _semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            ReportWaiting();
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var metadata = CreateMetadata(operationType, correlationId, projectId);
            var fileLease = await InterprocessFileLease.TryAcquireAsync(
                _lockPath,
                metadata,
                wait: true,
                PollingInterval,
                ReportWaiting,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("GPU lease could not be acquired.");

            Volatile.Write(ref _isBusy, 1);
            _logger.LogInformation(
                "GPU generation lease acquired. Operation={OperationType}; ProjectId={ProjectId}; SceneId={SceneId}; CorrelationId={CorrelationId}",
                operationType,
                projectId,
                sceneId,
                correlationId);
            _activityCenter?.AddLog("GPU alındı", $"Operation={operationType}; CorrelationId={correlationId}", GenerationLogLevel.Information);
            return new Lease(this, fileLease, operationType, projectId, sceneId, correlationId);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private static InterprocessLockOwnerMetadata CreateMetadata(
        GenerationOperationType operationType,
        string correlationId,
        int projectId)
    {
        using var process = Process.GetCurrentProcess();
        DateTime startTimeUtc;
        try
        {
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            startTimeUtc = DateTime.UtcNow;
        }

        return new InterprocessLockOwnerMetadata(
            Environment.ProcessId,
            startTimeUtc,
            Environment.MachineName,
            operationType.ToString(),
            correlationId,
            DateTime.UtcNow,
            projectId > 0 ? projectId : null);
    }

    private async ValueTask ReleaseAsync(
        InterprocessFileLease fileLease,
        GenerationOperationType operationType,
        int projectId,
        int sceneId,
        string correlationId)
    {
        try
        {
            await fileLease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _isBusy, 0);
            _semaphore.Release();
            _logger.LogInformation(
                "GPU generation lease released. Operation={OperationType}; ProjectId={ProjectId}; SceneId={SceneId}; CorrelationId={CorrelationId}",
                operationType,
                projectId,
                sceneId,
                correlationId);
            _activityCenter?.AddLog("GPU bırakıldı", $"Operation={operationType}; CorrelationId={correlationId}", GenerationLogLevel.Information);
        }
    }

    private sealed class Lease(
        GpuGenerationCoordinator owner,
        InterprocessFileLease fileLease,
        GenerationOperationType operationType,
        int projectId,
        int sceneId,
        string correlationId) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0
                ? owner.ReleaseAsync(fileLease, operationType, projectId, sceneId, correlationId)
                : ValueTask.CompletedTask;
    }
}
