using Director.Enums;

namespace Director.Services.Interfaces;

public interface IAutonomousGenerationStateMachine
{
    bool CanTransition(AutonomousGenerationRunStatus current, AutonomousGenerationRunStatus next);
    AutonomousGenerationStage ToStage(AutonomousGenerationRunStatus status);
    bool IsRunnable(AutonomousGenerationRunStatus status);
    bool IsTerminal(AutonomousGenerationRunStatus status);
}
