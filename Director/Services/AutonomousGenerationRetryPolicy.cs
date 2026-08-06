namespace Director.Services;

public sealed class AutonomousGenerationRetryPolicy
{
    public int MaxAttempts { get; }

    public AutonomousGenerationRetryPolicy(int maxAttempts = 3)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "Retry attempt count must be positive.");
        }

        MaxAttempts = maxAttempts;
    }

    public async Task ExecuteAsync(
        Func<int, CancellationToken, Task> action,
        Func<Exception, int, CancellationToken, Task>? onFailedAttempt = null,
        CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action(attempt, cancellationToken);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                lastException = ex;
                if (onFailedAttempt is not null)
                {
                    await onFailedAttempt(ex, attempt, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new InvalidOperationException("Autonomous step failed without an exception.");
    }
}
