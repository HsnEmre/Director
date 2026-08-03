using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Director.Data;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Director.Services;

public sealed class ProjectGenerationLeaseCoordinator : IProjectGenerationLeaseCoordinator
{
    public const string LockNamespace = "Director.ProjectGeneration.v1";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDbContextFactory<AppDbContext>? _dbContextFactory;
    private readonly ILogger<ProjectGenerationLeaseCoordinator> _logger;
    private readonly string _lockDirectory;
    private readonly SemaphoreSlim _identityGate = new(1, 1);
    private DatabaseIdentity? _databaseIdentity;

    public ProjectGenerationLeaseCoordinator(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<ProjectGenerationLeaseCoordinator> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _lockDirectory = GetDefaultLockDirectory();
    }

    public ProjectGenerationLeaseCoordinator(
        DatabaseIdentity databaseIdentity,
        ILogger<ProjectGenerationLeaseCoordinator> logger,
        string lockDirectory)
    {
        _databaseIdentity = databaseIdentity;
        _logger = logger;
        _lockDirectory = lockDirectory;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(int filmProjectId, CancellationToken cancellationToken = default)
    {
        if (filmProjectId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(filmProjectId));
        }

        var identity = await GetDatabaseIdentityAsync(cancellationToken).ConfigureAwait(false);
        var lockName = $"{LockNamespace}.{identity.Hash}.{filmProjectId}";
        var lockPath = Path.Combine(_lockDirectory, lockName + ".lock");
        var processLock = ProcessLocks.GetOrAdd(lockPath, _ => new SemaphoreSlim(1, 1));
        if (!await processLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new ProjectGenerationAlreadyRunningException(filmProjectId, identity.ShortHash);
        }

        try
        {
            var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            var metadata = CreateMetadata("ProjectGeneration", correlationId, filmProjectId);
            var fileLease = await InterprocessFileLease.TryAcquireAsync(
                lockPath,
                metadata,
                wait: false,
                TimeSpan.FromMilliseconds(100),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (fileLease is null)
            {
                throw new ProjectGenerationAlreadyRunningException(filmProjectId, identity.ShortHash);
            }

            _logger.LogInformation(
                "Project generation lease acquired. ProjectId={ProjectId}; DatabaseIdentity={DatabaseIdentity}; CorrelationId={CorrelationId}",
                filmProjectId,
                identity.ShortHash,
                correlationId);
            return new Lease(fileLease, processLock, _logger, filmProjectId, identity.ShortHash, correlationId);
        }
        catch
        {
            processLock.Release();
            throw;
        }
    }

    public static string GetDefaultLockDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Director", "Locks");

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

    private static InterprocessLockOwnerMetadata CreateMetadata(string operation, string correlationId, int projectId)
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
            operation,
            correlationId,
            DateTime.UtcNow,
            projectId);
    }

    private sealed class Lease(
        InterprocessFileLease fileLease,
        SemaphoreSlim processLock,
        ILogger logger,
        int projectId,
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
                "Project generation lease released. ProjectId={ProjectId}; DatabaseIdentity={DatabaseIdentity}; CorrelationId={CorrelationId}",
                projectId,
                databaseIdentity,
                correlationId);
        }
    }
}
