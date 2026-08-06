namespace Director.Services.Interfaces;

public interface IAutonomousGenerationOrchestrator
{
    Task RunAsync(int runId, string? workerId = null, CancellationToken cancellationToken = default);
}
