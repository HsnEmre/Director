using Director.Enums;
using Director.Services;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: InterprocessLockProbe gpu <lock-directory> <lock-namespace> | project <lock-directory> <database-hash> <project-id>");
    return 2;
}

IAsyncDisposable lease;
if (string.Equals(args[0], "gpu", StringComparison.OrdinalIgnoreCase))
{
    var coordinator = new GpuGenerationCoordinator(
        NullLogger<GpuGenerationCoordinator>.Instance,
        args[1],
        args[2]);
    lease = await coordinator.AcquireAsync(GenerationOperationType.OllamaText, 0, 0);
}
else if (string.Equals(args[0], "project", StringComparison.OrdinalIgnoreCase) &&
         args.Length >= 4 &&
         int.TryParse(args[3], out var projectId))
{
    var coordinator = new ProjectGenerationLeaseCoordinator(
        new DatabaseIdentity("test", args[2]),
        NullLogger<ProjectGenerationLeaseCoordinator>.Instance,
        args[1]);
    lease = await coordinator.AcquireAsync(projectId);
}
else
{
    Console.Error.WriteLine("Invalid probe arguments.");
    return 2;
}

await using (lease)
{
    Console.WriteLine("READY");
    await Console.Out.FlushAsync();
    await Console.In.ReadLineAsync();
}

return 0;

public sealed class InterprocessLockProbeMarker;
