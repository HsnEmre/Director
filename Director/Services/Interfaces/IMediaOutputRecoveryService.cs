using Director.WanGp;

namespace Director.Services.Interfaces;

public interface IMediaOutputRecoveryService
{
    Task<MediaOutputRecoveryPlan> PlanVideoRecoveryAsync(
        MediaOutputRecoveryRequest request,
        CancellationToken cancellationToken = default);

    Task<MediaOutputRecoveryWriteResult> WriteVideoRecoveryAsync(
        MediaOutputRecoveryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class MediaOutputRecoveryRequest
{
    public int? GenerationJobId { get; set; }
    public int? FilmProjectId { get; set; }
    public int? SceneId { get; set; }
    public int? Seed { get; set; }
    public bool Write { get; set; }
}

public sealed class MediaOutputRecoveryPlan
{
    public int GenerationJobId { get; set; }
    public int FilmProjectId { get; set; }
    public int SceneId { get; set; }
    public int SceneNumber { get; set; }
    public string JobStatus { get; set; } = string.Empty;
    public string CurrentPhase { get; set; } = string.Empty;
    public int? Seed { get; set; }
    public string? ExpectedOrTransientPath { get; set; }
    public bool TransientExists { get; set; }
    public string? ResolvedFinalPath { get; set; }
    public bool FinalExists { get; set; }
    public long? FinalSize { get; set; }
    public double? DurationSeconds { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public string IntendedDestination { get; set; } = string.Empty;
    public int ExistingVideoAssetCount { get; set; }
    public bool ExistingAssetForJob { get; set; }
    public bool RecoveryPossible { get; set; }
    public bool Ambiguous { get; set; }
    public IReadOnlyList<string> Evidence { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}

public sealed class MediaOutputRecoveryWriteResult
{
    public bool RecoverySucceeded { get; set; }
    public bool AlreadyRecovered { get; set; }
    public int GenerationJobId { get; set; }
    public int SceneId { get; set; }
    public int SceneNumber { get; set; }
    public bool SourcePreserved { get; set; }
    public long? SourceFileSize { get; set; }
    public string DestinationFileName { get; set; } = string.Empty;
    public bool DestinationExists { get; set; }
    public long? DestinationFileSize { get; set; }
    public bool FingerprintMatch { get; set; }
    public bool AssetCreated { get; set; }
    public int? SceneMediaAssetId { get; set; }
    public int? VersionNumber { get; set; }
    public bool IsSelected { get; set; }
    public string JobStatus { get; set; } = string.Empty;
    public string JobCurrentPhase { get; set; } = string.Empty;
    public int ExistingVideoAssetCount { get; set; }
    public int WanGpSubmitCount { get; set; }
    public int OllamaCallCount { get; set; }
    public int DbWriteCount { get; set; }
    public int FileCopyCount { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class MediaOutputRecoveryBusyException : Exception
{
    public MediaOutputRecoveryBusyException(int generationJobId)
        : base($"Recovery is already running for GenerationJobId={generationJobId}.")
    {
        GenerationJobId = generationJobId;
    }

    public int GenerationJobId { get; }
}

public sealed class MediaOutputRecoveryNotPossibleException : Exception
{
    public MediaOutputRecoveryNotPossibleException(string message) : base(message)
    {
    }
}

public sealed class MediaOutputRecoveryImportException : Exception
{
    public MediaOutputRecoveryImportException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

public sealed class MediaOutputRecoveryDbException : Exception
{
    public MediaOutputRecoveryDbException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}
