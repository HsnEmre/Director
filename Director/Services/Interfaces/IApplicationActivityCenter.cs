using System.Collections.ObjectModel;
using Director.Enums;
using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IApplicationActivityCenter
{
    ApplicationActivitySnapshot Snapshot { get; }
    ObservableCollection<ProductionLogEntry> Logs { get; }
    event EventHandler? Changed;
    void SetRuntimeStatus(WanGpRuntimeStatus status);
    void StartOperation(string operationName, int? projectId, string projectName, int? sceneId, int? sceneNumber);
    void SetActiveJob(int? jobId, string? externalJobId);
    void UpdateProgress(double progress, string phase, int? currentStep = null, int? totalSteps = null);
    void CompleteOperation(GenerationJobStatus status, string message);
    void AddLog(string phase, string message, GenerationLogLevel level = GenerationLogLevel.Information);
    void ClearLogs();
    void SetModelDiscoveryStatus(string status);
    void SetError(string message);
}
