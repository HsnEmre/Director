using Director.Enums;
using Director.Services;

namespace Director.Tests;

public sealed class AutonomousGenerationStateMachineTests
{
    [Fact]
    public void CanTransition_AllowsForwardPipelineAndOperationalStops()
    {
        var stateMachine = new AutonomousGenerationStateMachine();

        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.Pending, AutonomousGenerationRunStatus.Validating));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.Validating, AutonomousGenerationRunStatus.GeneratingStoryNarrative));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingStoryNarrative, AutonomousGenerationRunStatus.GeneratingCharacters));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingCharacters, AutonomousGenerationRunStatus.GeneratingNarrativeScenes));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingNarrativeScenes, AutonomousGenerationRunStatus.GeneratingImagePrompts));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingImagePrompts, AutonomousGenerationRunStatus.GeneratingVideoPrompts));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingVideoPrompts, AutonomousGenerationRunStatus.GeneratingImages));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingImages, AutonomousGenerationRunStatus.GeneratingVideos));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.Validating, AutonomousGenerationRunStatus.GeneratingStory));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingStory, AutonomousGenerationRunStatus.GeneratingScenes));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingScenes, AutonomousGenerationRunStatus.GeneratingImages));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingImages, AutonomousGenerationRunStatus.GeneratingVideos));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingVideos, AutonomousGenerationRunStatus.GeneratingAudio));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingAudio, AutonomousGenerationRunStatus.Finalizing));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.Finalizing, AutonomousGenerationRunStatus.Completed));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingVideos, AutonomousGenerationRunStatus.Paused));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingVideos, AutonomousGenerationRunStatus.CancelRequested));
    }

    [Fact]
    public void CanTransition_RejectsBackwardOrTerminalResumeWithoutExplicitRetry()
    {
        var stateMachine = new AutonomousGenerationStateMachine();

        Assert.False(stateMachine.CanTransition(AutonomousGenerationRunStatus.GeneratingVideos, AutonomousGenerationRunStatus.GeneratingImages));
        Assert.False(stateMachine.CanTransition(AutonomousGenerationRunStatus.Completed, AutonomousGenerationRunStatus.Pending));
        Assert.True(stateMachine.CanTransition(AutonomousGenerationRunStatus.Failed, AutonomousGenerationRunStatus.Pending));
    }
}
