using Director.Enums;
using Director.Services.Interfaces;

namespace Director.Services;

public sealed class AutonomousGenerationStateMachine : IAutonomousGenerationStateMachine
{
    private static readonly IReadOnlyDictionary<AutonomousGenerationRunStatus, AutonomousGenerationRunStatus[]> AllowedTransitions =
        new Dictionary<AutonomousGenerationRunStatus, AutonomousGenerationRunStatus[]>
        {
            [AutonomousGenerationRunStatus.Pending] =
            [
                AutonomousGenerationRunStatus.Validating,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.Validating] =
            [
                AutonomousGenerationRunStatus.GeneratingStoryNarrative,
                AutonomousGenerationRunStatus.GeneratingCharacters,
                AutonomousGenerationRunStatus.GeneratingNarrativeScenes,
                AutonomousGenerationRunStatus.GeneratingImagePrompts,
                AutonomousGenerationRunStatus.GeneratingVideoPrompts,
                AutonomousGenerationRunStatus.GeneratingStory,
                AutonomousGenerationRunStatus.GeneratingScenes,
                AutonomousGenerationRunStatus.GeneratingImages,
                AutonomousGenerationRunStatus.GeneratingVideos,
                AutonomousGenerationRunStatus.GeneratingAudio,
                AutonomousGenerationRunStatus.Finalizing,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingStoryNarrative] =
            [
                AutonomousGenerationRunStatus.GeneratingCharacters,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingCharacters] =
            [
                AutonomousGenerationRunStatus.GeneratingNarrativeScenes,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingNarrativeScenes] =
            [
                AutonomousGenerationRunStatus.GeneratingImagePrompts,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingImagePrompts] =
            [
                AutonomousGenerationRunStatus.GeneratingVideoPrompts,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingStory] =
            [
                AutonomousGenerationRunStatus.GeneratingScenes,
                AutonomousGenerationRunStatus.GeneratingImages,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingScenes] =
            [
                AutonomousGenerationRunStatus.GeneratingImages,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingImages] =
            [
                AutonomousGenerationRunStatus.GeneratingVideoPrompts,
                AutonomousGenerationRunStatus.GeneratingVideos,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingVideoPrompts] =
            [
                AutonomousGenerationRunStatus.GeneratingImages,
                AutonomousGenerationRunStatus.GeneratingVideos,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingVideos] =
            [
                AutonomousGenerationRunStatus.GeneratingAudio,
                AutonomousGenerationRunStatus.Finalizing,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.GeneratingAudio] =
            [
                AutonomousGenerationRunStatus.Finalizing,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.Finalizing] =
            [
                AutonomousGenerationRunStatus.Completed,
                AutonomousGenerationRunStatus.Failed,
                AutonomousGenerationRunStatus.CancelRequested,
                AutonomousGenerationRunStatus.Paused
            ],
            [AutonomousGenerationRunStatus.CancelRequested] =
            [
                AutonomousGenerationRunStatus.Cancelled
            ],
            [AutonomousGenerationRunStatus.Paused] =
            [
                AutonomousGenerationRunStatus.Pending,
                AutonomousGenerationRunStatus.CancelRequested
            ],
            [AutonomousGenerationRunStatus.Failed] =
            [
                AutonomousGenerationRunStatus.Pending,
                AutonomousGenerationRunStatus.CancelRequested
            ]
        };

    public bool CanTransition(AutonomousGenerationRunStatus current, AutonomousGenerationRunStatus next)
    {
        if (current == next)
        {
            return true;
        }

        return AllowedTransitions.TryGetValue(current, out var nextStatuses) && nextStatuses.Contains(next);
    }

    public AutonomousGenerationStage ToStage(AutonomousGenerationRunStatus status) => status switch
    {
        AutonomousGenerationRunStatus.Pending => AutonomousGenerationStage.Pending,
        AutonomousGenerationRunStatus.Validating => AutonomousGenerationStage.Validating,
        AutonomousGenerationRunStatus.GeneratingStoryNarrative => AutonomousGenerationStage.GeneratingStoryNarrative,
        AutonomousGenerationRunStatus.GeneratingCharacters => AutonomousGenerationStage.GeneratingCharacters,
        AutonomousGenerationRunStatus.GeneratingNarrativeScenes => AutonomousGenerationStage.GeneratingNarrativeScenes,
        AutonomousGenerationRunStatus.GeneratingImagePrompts => AutonomousGenerationStage.GeneratingImagePrompts,
        AutonomousGenerationRunStatus.GeneratingVideoPrompts => AutonomousGenerationStage.GeneratingVideoPrompts,
        AutonomousGenerationRunStatus.GeneratingStory => AutonomousGenerationStage.GeneratingStory,
        AutonomousGenerationRunStatus.GeneratingScenes => AutonomousGenerationStage.GeneratingScenes,
        AutonomousGenerationRunStatus.GeneratingImages => AutonomousGenerationStage.GeneratingImages,
        AutonomousGenerationRunStatus.GeneratingVideos => AutonomousGenerationStage.GeneratingVideos,
        AutonomousGenerationRunStatus.GeneratingAudio => AutonomousGenerationStage.GeneratingAudio,
        AutonomousGenerationRunStatus.Finalizing => AutonomousGenerationStage.Finalizing,
        AutonomousGenerationRunStatus.Completed => AutonomousGenerationStage.Completed,
        AutonomousGenerationRunStatus.Failed => AutonomousGenerationStage.Failed,
        AutonomousGenerationRunStatus.CancelRequested => AutonomousGenerationStage.CancelRequested,
        AutonomousGenerationRunStatus.Cancelled => AutonomousGenerationStage.Cancelled,
        AutonomousGenerationRunStatus.Paused => AutonomousGenerationStage.Paused,
        _ => AutonomousGenerationStage.Pending
    };

    public bool IsRunnable(AutonomousGenerationRunStatus status) => status is
        AutonomousGenerationRunStatus.Pending or
        AutonomousGenerationRunStatus.Validating or
        AutonomousGenerationRunStatus.GeneratingStoryNarrative or
        AutonomousGenerationRunStatus.GeneratingCharacters or
        AutonomousGenerationRunStatus.GeneratingNarrativeScenes or
        AutonomousGenerationRunStatus.GeneratingImagePrompts or
        AutonomousGenerationRunStatus.GeneratingVideoPrompts or
        AutonomousGenerationRunStatus.GeneratingStory or
        AutonomousGenerationRunStatus.GeneratingScenes or
        AutonomousGenerationRunStatus.GeneratingImages or
        AutonomousGenerationRunStatus.GeneratingVideos or
        AutonomousGenerationRunStatus.GeneratingAudio or
        AutonomousGenerationRunStatus.Finalizing;

    public bool IsTerminal(AutonomousGenerationRunStatus status) => status is
        AutonomousGenerationRunStatus.Completed or
        AutonomousGenerationRunStatus.Failed or
        AutonomousGenerationRunStatus.Cancelled;
}
