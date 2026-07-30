using System.Collections.ObjectModel;
using System.Windows;
using Director.Enums;
using Director.Services.Interfaces;
using Director.WanGp;

namespace Director.Services;

public sealed class ApplicationActivityCenter : IApplicationActivityCenter
{
    private const int MaxLogs = 1000;
    private readonly object _lock = new();

    public ApplicationActivitySnapshot Snapshot { get; } = new();
    public ObservableCollection<ProductionLogEntry> Logs { get; } = new();
    public event EventHandler? Changed;

    public void SetRuntimeStatus(WanGpRuntimeStatus status)
    {
        Snapshot.McpState = status.McpState;
        Snapshot.GuiState = status.GuiState;
        AddLog("WanGP", status.Message, status.IsReady ? GenerationLogLevel.Success : GenerationLogLevel.Information);
        RaiseChanged();
    }

    public void StartOperation(string operationName, int? projectId, string projectName, int? sceneId, int? sceneNumber)
    {
        Snapshot.OperationName = operationName;
        Snapshot.ActiveProjectId = projectId;
        Snapshot.ProjectName = projectName;
        Snapshot.ActiveSceneId = sceneId;
        Snapshot.SceneNumber = sceneNumber;
        Snapshot.ActiveJobId = null;
        Snapshot.ActiveExternalJobId = null;
        Snapshot.HasActiveOperation = true;
        Snapshot.StartedAt = DateTime.Now;
        Snapshot.LastActivityAt = DateTime.Now;
        Snapshot.OperationStatus = GenerationJobStatus.Running;
        Snapshot.Progress = 0;
        Snapshot.CurrentPhase = operationName;
        AddLog(operationName, "İşlem başladı.", GenerationLogLevel.Information);
        RaiseChanged();
    }

    public void UpdateProgress(double progress, string phase, int? currentStep = null, int? totalSteps = null)
    {
        Snapshot.Progress = progress;
        Snapshot.CurrentPhase = phase;
        Snapshot.CurrentStep = currentStep;
        Snapshot.TotalSteps = totalSteps;
        Snapshot.LastActivityAt = DateTime.Now;
        RaiseChanged();
    }

    public void SetActiveJob(int? jobId, string? externalJobId)
    {
        Snapshot.ActiveJobId = jobId;
        Snapshot.ActiveExternalJobId = externalJobId;
        Snapshot.LastActivityAt = DateTime.Now;
        RaiseChanged();
    }

    public void CompleteOperation(GenerationJobStatus status, string message)
    {
        Snapshot.OperationStatus = status;
        Snapshot.HasActiveOperation = false;
        Snapshot.ActiveJobId = null;
        Snapshot.ActiveExternalJobId = null;
        Snapshot.ActiveSceneId = null;
        Snapshot.LastActivityAt = DateTime.Now;
        AddLog(status.ToString(), message, status == GenerationJobStatus.Completed ? GenerationLogLevel.Success : GenerationLogLevel.Warning);
        RaiseChanged();
    }

    public void AddLog(string phase, string message, GenerationLogLevel level = GenerationLogLevel.Information)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        void Add()
        {
            lock (_lock)
            {
                Logs.Add(new ProductionLogEntry
                {
                    Timestamp = DateTime.Now,
                    Phase = phase,
                    Source = MapSource(phase),
                    Message = message,
                    Level = level
                });

                while (Logs.Count > MaxLogs)
                {
                    Logs.RemoveAt(0);
                }
            }

            RaiseChanged();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Add);
            return;
        }

        Add();
    }

    public void SetModelDiscoveryStatus(string status)
    {
        Snapshot.ModelDiscoveryStatus = status;
        Snapshot.LastActivityAt = DateTime.Now;
        AddLog("Modeller", status, GenerationLogLevel.Information);
        RaiseChanged();
    }

    public void SetError(string message)
    {
        Snapshot.LastError = message;
        Snapshot.LastActivityAt = DateTime.Now;
        AddLog("Hata", message, GenerationLogLevel.Error);
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private static string MapSource(string phase)
    {
        if (phase.Contains("WanGP", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("MCP", StringComparison.OrdinalIgnoreCase))
        {
            return "WanGP";
        }

        if (phase.Contains("Output", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("Dosya", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("Import", StringComparison.OrdinalIgnoreCase))
        {
            return "Dosya";
        }

        if (phase.Contains("Gorsel", StringComparison.OrdinalIgnoreCase) ||
            phase.Contains("Inference", StringComparison.OrdinalIgnoreCase))
        {
            return "Gorsel";
        }

        if (phase.Contains("Veritabani", StringComparison.OrdinalIgnoreCase))
        {
            return "Veritabani";
        }

        if (phase.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return "Ollama";
        }

        return "Sistem";
    }
}
