using System.IO;
using System.Text.Json;

namespace Director.Services;

public sealed record InterprocessLockOwnerMetadata(
    int Pid,
    DateTime ProcessStartTimeUtc,
    string MachineName,
    string Operation,
    string CorrelationId,
    DateTime AcquiredUtc,
    int? ProjectId);

public sealed class InterprocessFileLease : IAsyncDisposable
{
    private const long LockOffset = 0;
    private const long LockLength = 1;
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);
    private FileStream? _stream;

    private InterprocessFileLease(FileStream stream, string lockPath)
    {
        _stream = stream;
        LockPath = lockPath;
    }

    public string LockPath { get; }

    public static async ValueTask<InterprocessFileLease?> TryAcquireAsync(
        string lockPath,
        InterprocessLockOwnerMetadata metadata,
        bool wait,
        TimeSpan pollingInterval,
        Action? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollingInterval));
        }

        var directory = Path.GetDirectoryName(lockPath)
            ?? throw new InvalidOperationException("Lock path must have a directory.");
        Directory.CreateDirectory(directory);
        var waitingReported = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);
                stream.Lock(LockOffset, LockLength);
                var lease = new InterprocessFileLease(stream, lockPath);
                stream = null;
                await lease.WriteMetadataBestEffortAsync(metadata, cancellationToken).ConfigureAwait(false);
                return lease;
            }
            catch (IOException)
            {
                stream?.Dispose();
                if (!wait)
                {
                    return null;
                }

                if (!waitingReported)
                {
                    waitingReported = true;
                    onWaiting?.Invoke();
                }

                await Task.Delay(pollingInterval, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    public static bool TryReadMetadata(string lockPath, out InterprocessLockOwnerMetadata? metadata)
    {
        metadata = null;
        try
        {
            using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= LockLength)
            {
                return false;
            }

            stream.Position = LockLength;
            metadata = JsonSerializer.Deserialize<InterprocessLockOwnerMetadata>(stream, MetadataJsonOptions);
            return metadata is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            stream.Unlock(LockOffset, LockLength);
        }
        catch (IOException)
        {
            // Closing the OS handle is the authoritative release fallback.
        }
        finally
        {
            stream.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task WriteMetadataBestEffortAsync(InterprocessLockOwnerMetadata metadata, CancellationToken cancellationToken)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.SetLength(LockLength);
            stream.Position = LockLength;
            await JsonSerializer.SerializeAsync(stream, metadata, MetadataJsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Metadata is diagnostic only; the byte-range OS lock remains authoritative.
        }
    }
}
