using Director.Data;
using Director.Dtos.StoryGeneration;
using Director.Ollama;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace Director.Tests;

public sealed class StoryGenerationViewModelLeaseTests
{
    [Fact]
    public async Task TypedProjectBusy_ReleasesUiStateAndShowsFriendlyMessage()
    {
        var messages = new RecordingMessageService();
        var viewModel = new StoryGenerationViewModel(
            null!,
            null!,
            new BusyStoryGenerationService(),
            messages,
            new NoopNavigationService(),
            Microsoft.Extensions.Options.Options.Create(new OllamaOptions()))
        {
            FilmProjectId = 9
        };

        await viewModel.GenerateStoryAsync();

        Assert.False(viewModel.IsBusy);
        Assert.Equal("Proje üretimi kullanımda", viewModel.Phase);
        Assert.Equal(ProjectGenerationAlreadyRunningException.UserMessage, viewModel.ProgressMessage);
        Assert.Equal(ProjectGenerationAlreadyRunningException.UserMessage, messages.LastError);
        Assert.True(viewModel.GenerateStoryCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    private sealed class BusyStoryGenerationService : IStoryGenerationService
    {
        public Task<StoryGenerationProgressResult> GenerateStoryAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromException<StoryGenerationProgressResult>(new ProjectGenerationAlreadyRunningException(filmProjectId, "test"));

        public Task<StoryGenerationProgressResult> GenerateAllMissingScenesAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateStoryAsync(filmProjectId, progress, cancellationToken);

        public Task<StoryGenerationProgressResult> GenerateNextMissingSceneAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateStoryAsync(filmProjectId, progress, cancellationToken);

        public Task<StoryGenerationProgressResult> GenerateUpToMissingScenesAsync(int filmProjectId, int maximumSceneCount, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateStoryAsync(filmProjectId, progress, cancellationToken);
    }

    private sealed class RecordingMessageService : IMessageService
    {
        public string LastError { get; private set; } = string.Empty;
        public void ShowInfo(string message, string title = "Director") { }
        public void ShowError(string message, string title = "Director") => LastError = message;
        public bool Confirm(string message, string title = "Director") => true;
    }

    private sealed class NoopNavigationService : INavigationService
    {
        public object? CurrentViewModel => null;
        public string CurrentStep => string.Empty;
        public int? CurrentProjectId => null;
        public Task NavigateToProjectSetupAsync(int? projectId = null) => Task.CompletedTask;
        public Task NavigateToStoryGenerationAsync(int projectId) => Task.CompletedTask;
        public Task NavigateToProjectHistoryAsync() => Task.CompletedTask;
        public Task NavigateToProductionAsync(int projectId) => Task.CompletedTask;
    }
}
