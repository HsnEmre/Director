using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Director.Enums;
using Director.Services.Interfaces;
using Director.WanGp;

namespace Director.Services;

public sealed class ApplicationActivityCenter : IApplicationActivityCenter
{
    private const int MaxLogs = 1000;
    private const int MaxPendingLogs = 1000;
    private readonly object _lock = new();
    private readonly Queue<ProductionLogEntry> _pendingLogs = new();
    private int _drainScheduled;

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

        var entry = new ProductionLogEntry
        {
            Timestamp = DateTime.Now,
            Phase = phase,
            Source = MapSource(phase),
            Message = message,
            Level = level
        };

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            EnqueueLog(entry);
            ScheduleDrain(dispatcher);
            return;
        }

        AddEntry(entry);
    }

    public void ClearLogs()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            lock (_lock)
            {
                _pendingLogs.Clear();
            }

            ScheduleUiWork(dispatcher, ClearLogsOnCurrentThread);
            return;
        }

        ClearLogsOnCurrentThread();
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

    private void EnqueueLog(ProductionLogEntry entry)
    {
        lock (_lock)
        {
            _pendingLogs.Enqueue(entry);
            while (_pendingLogs.Count > MaxPendingLogs)
            {
                _pendingLogs.Dequeue();
            }
        }
    }

    private void ScheduleDrain(Dispatcher dispatcher)
    {
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 1)
        {
            return;
        }

        if (!ScheduleUiWork(dispatcher, DrainPendingLogs))
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
        }
    }

    private bool ScheduleUiWork(Dispatcher dispatcher, Action action)
    {
        try
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Background);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void DrainPendingLogs()
    {
        try
        {
            while (TryDequeue(out var entry))
            {
                AddEntry(entry);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && HasPendingLogs())
        {
            ScheduleDrain(dispatcher);
        }
    }

    private bool TryDequeue(out ProductionLogEntry entry)
    {
        lock (_lock)
        {
            if (_pendingLogs.Count > 0)
            {
                entry = _pendingLogs.Dequeue();
                return true;
            }
        }

        entry = default!;
        return false;
    }

    private bool HasPendingLogs()
    {
        lock (_lock)
        {
            return _pendingLogs.Count > 0;
        }
    }

    private void AddEntry(ProductionLogEntry entry)
    {
        lock (_lock)
        {
            Logs.Add(entry);
            while (Logs.Count > MaxLogs)
            {
                Logs.RemoveAt(0);
            }
        }

        RaiseChanged();
    }

    private void ClearLogsOnCurrentThread()
    {
        lock (_lock)
        {
            Logs.Clear();
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

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
