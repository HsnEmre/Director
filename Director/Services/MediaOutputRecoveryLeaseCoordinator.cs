using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Director.Data;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class MediaOutputRecoveryLeaseCoordinator : IMediaOutputRecoveryLeaseCoordinator
{
    public const string LockNamespace = "Director.MediaOutputRecovery.v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
    private readonly ILogger<MediaOutputRecoveryLeaseCoordinator> _logger;
    private readonly string _lockDirectory;
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private DatabaseIdentity? _databaseIdentity;

    public MediaOutputRecoveryLeaseCoordinator(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<MediaOutputRecoveryLeaseCoordinator> logger)
        : this(dbContextFactory, logger, ProjectGenerationLeaseCoordinator.GetDefaultLockDirectory())
    {
    }

    public MediaOutputRecoveryLeaseCoordinator(
        IDbContextFactory<AppDbContext>? dbContextFactory,
        ILogger<MediaOutputRecoveryLeaseCoordinator> logger,
        string lockDirectory,
        DatabaseIdentity? databaseIdentity = null)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _lockDirectory = lockDirectory;
        _databaseIdentity = databaseIdentity;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(int generationJobId, CancellationToken cancellationToken = default)
    {
        if (generationJobId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generationJobId));
        }

        var identity = await GetDatabaseIdentityAsync(cancellationToken).ConfigureAwait(false);
        var lockName = $"{LockNamespace}.{identity.Hash}.{generationJobId}";
        var lockPath = Path.Combine(_lockDirectory, lockName + ".lock");
        var processLock = ProcessLocks.GetOrAdd(lockPath, _ => new SemaphoreSlim(1, 1));
        if (!await processLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new MediaOutputRecoveryBusyException(generationJobId);
        }

        try
        {
            var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var fileLease = await InterprocessFileLease.TryAcquireAsync(
                lockPath,
                CreateMetadata(correlationId, generationJobId),
                wait: false,
                TimeSpan.FromMilliseconds(100),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (fileLease is null)
            {
                throw new MediaOutputRecoveryBusyException(generationJobId);
            }

            _logger.LogInformation(
                "Media output recovery lease acquired. JobId={GenerationJobId}; DatabaseIdentity={DatabaseIdentity}; CorrelationId={CorrelationId}",
                generationJobId,
                identity.ShortHash,
                correlationId);
            return new Lease(fileLease, processLock, _logger, generationJobId, identity.ShortHash, correlationId);
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    private async Task<DatabaseIdentity> GetDatabaseIdentityAsync(CancellationToken cancellationToken)
    {
        if (_databaseIdentity is not null)
        {
            return _databaseIdentity;
        }

        await _identityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_databaseIdentity is null)
            {
                if (_dbContextFactory is null)
                {
                    throw new InvalidOperationException("Database identity source is unavailable.");
                }

                await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                _databaseIdentity = DatabaseIdentity.Create(db.Database.ProviderName, db.Database.GetDbConnection().ConnectionString);
            }

            return _databaseIdentity;
        }
        finally
        {
            _identityGate.Release();
        }
    }

    private static InterprocessLockOwnerMetadata CreateMetadata(string correlationId, int generationJobId)
    {
        using var process = Process.GetCurrentProcess();
        DateTime processStartUtc;
        try
        {
            processStartUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            processStartUtc = DateTime.UtcNow;
        }

        return new InterprocessLockOwnerMetadata(
            Environment.ProcessId,
            processStartUtc,
            Environment.MachineName,
            "MediaOutputRecovery",
            correlationId,
            DateTime.UtcNow,
            generationJobId);
    }

    private sealed class Lease(
        InterprocessFileLease fileLease,
        SemaphoreSlim processLock,
        ILogger logger,
        int generationJobId,
        string databaseIdentity,
        string correlationId) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await fileLease.DisposeAsync().ConfigureAwait(false);
            processLock.Release();
            logger.LogInformation(
                "Media output recovery lease released. JobId={GenerationJobId}; DatabaseIdentity={DatabaseIdentity}; CorrelationId={CorrelationId}",
                generationJobId,
                databaseIdentity,
                correlationId);
        }
    }
}
