using Director.Enums;

namespace Director.WanGp;

public enum WanGpMcpConnectionState
{
    Unknown = 0,
    Connecting = 1,
    Connected = 2,
    Disconnected = 3,
    Starting = 4,
    PortConflict = 5,
    InvalidConfiguration = 6
}

public enum WanGpGuiState
{
    Unknown = 0,
    Open = 1,
    Closed = 2
}

public enum WanGpModelInstallStatus
{
    Installed = 0,
    Partial = 1,
    Missing = 2,
    Unknown = 3
}

public sealed class WanGpRuntimeStatus
{
    public WanGpGuiState GuiState { get; set; }
    public WanGpMcpConnectionState McpState { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public bool IsOwnedProcess { get; set; }
    public int? ProcessId { get; set; }
    public IReadOnlyList<string> Tools { get; set; } = [];
}

public sealed class WanGpLocalModelInventoryItem
{
    public string ModelType { get; set; } = string.Empty;
    public WanGpModelInstallStatus Status { get; set; } = WanGpModelInstallStatus.Unknown;
    public string? CheckpointPath { get; set; }
    public string Evidence { get; set; } = string.Empty;
    public DateTime CheckedAt { get; set; } = DateTime.Now;
}

public sealed class WanGpModelSelectionItem
{
    public string ModelType { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty;
    public string MainOutput { get; set; } = string.Empty;
    public string Inputs { get; set; } = string.Empty;
    public WanGpModelInstallStatus InstallStatus { get; set; } = WanGpModelInstallStatus.Unknown;
    public string? CheckpointPath { get; set; }
    public bool SupportsTextToImage { get; set; }
    public bool SupportsReferenceImage { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public bool IsSelectable => InstallStatus == WanGpModelInstallStatus.Installed;
    public string Summary => $"{DisplayName} ({ModelType})";
}

public sealed class ApplicationActivitySnapshot
{
    public int? ActiveProjectId { get; set; }
    public int? ActiveSceneId { get; set; }
    public int? ActiveJobId { get; set; }
    public string? ActiveExternalJobId { get; set; }
    public bool HasActiveOperation { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public int? SceneNumber { get; set; }
    public GenerationJobStatus? OperationStatus { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public double Progress { get; set; }
    public string CurrentPhase { get; set; } = string.Empty;
    public int? CurrentStep { get; set; }
    public int? TotalSteps { get; set; }
    public string SelectedModel { get; set; } = string.Empty;
    public string OutputDetectionStatus { get; set; } = string.Empty;
    public WanGpMcpConnectionState McpState { get; set; }
    public WanGpGuiState GuiState { get; set; }
    public string ModelDiscoveryStatus { get; set; } = string.Empty;
    public string? LastError { get; set; }
}

public sealed class ProductionLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public GenerationLogLevel Level { get; set; }
    public string Phase { get; set; } = string.Empty;
    public string Source { get; set; } = "Sistem";
    public string Message { get; set; } = string.Empty;
}
