using Director.Dtos.Autonomous;
using Director.Dtos.MediaGeneration;
using Director.Dtos.StoryGeneration;
using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class AutonomousGenerationOrchestratorTests
{
    [Fact]
    public async Task RunAsync_CompletesPipeline_WithStopOnFailureRequests()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.Equal(1, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(1, storyService.GenerateStoryCharactersCallCount);
        Assert.Equal(1, storyService.GenerateNarrativeScenesCallCount);
        Assert.Equal(1, storyService.GenerateImagePromptsCallCount);
        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(
            ["story-narrative", "characters", "narrative-scenes", "image-prompts", "video-prompts"],
            storyService.StageCalls);
        Assert.Equal(1, imageService.GenerateCallCount);
        Assert.True(imageService.LastRequest?.StopOnError);
        Assert.Equal(1, videoService.GenerateCallCount);
        Assert.True(videoService.LastRequest?.StopOnFailure);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.ImageStatus);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.VideoStatus);
        Assert.Equal(AutonomousWorkItemStatus.Skipped, runService.WorkItem.AudioStatus);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.FinalizationStatus);
    }

    [Fact]
    public async Task RunAsync_ResumeSkipsExistingValidAssets_AndKeepsIdempotency()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.SelectedImageAsset = files.CreateAsset(601, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        runService.SelectedVideoAsset = files.CreateAsset(602, MediaType.Video, MediaAssetRole.GeneratedSilentVideo, selected: true);
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, new FakeStoryGenerationService(), imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(0, videoService.GenerateCallCount);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.ImageStatus);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.VideoStatus);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task RunAsync_AutonomousImageGenerationAutoSelectsProducedAsset_AndKeepsSingleSelectedImage()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.AddImageAsset(files.CreateAsset(600, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true, valid: false));
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(1, imageService.GenerateCallCount);
        Assert.True(imageService.LastRequest?.AutoSelectOutput);
        var selectedImages = runService.ImageAssets.Where(asset => asset.SceneId == runService.Scene.Id && asset.MediaType == MediaType.Image && asset.IsSelected).ToList();
        var selected = Assert.Single(selectedImages);
        Assert.NotEqual(600, selected.Id);
        Assert.Equal(selected.Id, runService.WorkItem.ImageMediaAssetId);
        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
    }

    [Fact]
    public async Task RunAsync_TransientWanGpMcpImageFailure_RunsRuntimeRecoveryAndRetriesCurrentScene()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingImages;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingImages;
        var imageService = new FakeImageGenerationService(runService, files)
        {
            FailuresBeforeSuccess = 1
        };
        var runtime = new FakeWanGpRuntimeCoordinator();
        var orchestrator = CreateOrchestrator(
            runService,
            new FakeStoryGenerationService(),
            imageService,
            new FakeVideoGenerationService(runService, files),
            runtimeCoordinator: runtime);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(2, imageService.GenerateCallCount);
        Assert.True(runtime.EnsureReadyCallCount >= 2);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.ImageStatus);
        Assert.NotNull(runService.WorkItem.ImageMediaAssetId);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.True(string.IsNullOrWhiteSpace(runService.Run.LastError));
    }

    [Fact]
    public async Task RunAsync_ResumeReconcilesExistingUnselectedImage_WithoutRegenerating()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingImages;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingImages;
        var existing = files.CreateAsset(610, MediaType.Image, MediaAssetRole.ReferenceImage, selected: false);
        runService.AddImageAsset(existing);
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.True(existing.IsSelected);
        Assert.Equal(existing.Id, runService.WorkItem.ImageMediaAssetId);
        Assert.Equal(0, storyService.GenerateVideoPromptsCallCount);
    }

    [Fact]
    public async Task RunAsync_DoesNotStartVideoPromptGeneration_WhenAnySceneLacksImagePrompt()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideoPrompts;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideoPrompts;
        runService.Scenes.Add(new FilmScene
        {
            Id = 12,
            FilmProjectId = 7,
            SceneNumber = 2,
            DurationSeconds = 5,
            ImagePrompt = string.Empty,
            ImageNegativePrompt = string.Empty,
            VideoPrompt = string.Empty,
            DialogueJson = "[]"
        });
        await runService.EnsureSceneWorkItemsAsync(runService.Run.Id);
        var storyService = new FakeStoryGenerationService();
        var orchestrator = CreateOrchestrator(runService, storyService, new FakeImageGenerationService(runService, files), new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(AutonomousGenerationRunStatus.Failed, runService.Run.Status);
        Assert.Contains("image prompt", runService.Run.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_VideoPromptGenerationDoesNotRequireImageAsset()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideoPrompts;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideoPrompts;
        runService.ImageAssets.Clear();
        runService.Scene.VideoPrompt = string.Empty;
        runService.Scene.VideoNegativePrompt = string.Empty;
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                Assert.Equal("image", runService.Scene.ImagePrompt);
                runService.Scene.VideoPrompt = "video from image prompt context";
                runService.Scene.VideoNegativePrompt = "video negative";
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(1, imageService.GenerateCallCount);
    }

    [Fact]
    public async Task RunAsync_ResumeWithImagesCompleteAndEmptyVideoPrompt_BeginsAtSceneOneVideoPrompt()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingImages;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingImages;
        runService.Scene.VideoPrompt = string.Empty;
        runService.SelectedImageAsset = files.CreateAsset(630, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                Assert.Equal(1, runService.Scenes.First(scene => string.IsNullOrWhiteSpace(scene.VideoPrompt)).SceneNumber);
                runService.Scene.VideoPrompt = "video from image prompt context";
                runService.Scene.VideoNegativePrompt = "video negative";
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal("video from image prompt context", runService.Scene.VideoPrompt);
    }

    [Fact]
    public async Task RunAsync_NewRunWithFailedRunHistoryState_UsesProjectCheckpointsAndStartsAtVideoPromptSceneOne()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Pending;
        runService.Run.CurrentStage = AutonomousGenerationStage.Pending;
        runService.Run.LastError = string.Empty;
        runService.Project.CalculatedClipCount = 6;
        runService.Scene.VideoPrompt = string.Empty;
        runService.Scene.VideoNegativePrompt = string.Empty;
        runService.AddImageAsset(files.CreateAsset(710, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true, sceneId: 11));

        for (var sceneNumber = 2; sceneNumber <= 6; sceneNumber++)
        {
            var sceneId = 10 + sceneNumber;
            runService.Scenes.Add(new FilmScene
            {
                Id = sceneId,
                FilmProjectId = 7,
                SceneNumber = sceneNumber,
                DurationSeconds = 5,
                ImagePrompt = $"image {sceneNumber}",
                ImageNegativePrompt = $"image negative {sceneNumber}",
                VideoPrompt = string.Empty,
                VideoNegativePrompt = string.Empty,
                DialogueJson = "[]"
            });
            runService.AddImageAsset(files.CreateAsset(710 + sceneNumber, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true, sceneId: sceneId));
        }

        runService.Checkpoint = new AutonomousProjectCheckpoint
        {
            FilmProjectId = 7,
            ExpectedSceneCount = 6,
            SceneCount = 6,
            HasValidStory = true,
            HasValidCharacters = true,
            FirstMissingVideoPromptSceneNumber = 1,
            FirstMissingSelectedVideoSceneNumber = 1
        };
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                Assert.Equal(1, runService.Scenes.First(scene => string.IsNullOrWhiteSpace(scene.VideoPrompt)).SceneNumber);
                foreach (var scene in runService.Scenes)
                {
                    scene.VideoPrompt = $"video {scene.SceneNumber}";
                    scene.VideoNegativePrompt = $"video negative {scene.SceneNumber}";
                }
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(
            ["video-prompts"],
            storyService.StageCalls);
        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(0, storyService.GenerateStoryCharactersCallCount);
        Assert.Equal(0, storyService.GenerateNarrativeScenesCallCount);
        Assert.Equal(0, storyService.GenerateImagePromptsCallCount);
        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.All(runService.WorkItems, item =>
        {
            Assert.Equal(AutonomousWorkItemStatus.Completed, item.ImageStatus);
            Assert.NotNull(item.ImageMediaAssetId);
            Assert.Equal(0, item.ImageAttemptCount);
        });
        Assert.Contains(AutonomousGenerationRunStatus.GeneratingVideoPrompts, runService.Transitions);
        Assert.DoesNotContain(AutonomousGenerationRunStatus.GeneratingStoryNarrative, runService.Transitions);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task RunAsync_ResumeAfterVideoPromptValidationFailure_ContinuesAtSceneFour()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Pending;
        runService.Run.CurrentStage = AutonomousGenerationStage.Pending;
        runService.Project.CalculatedClipCount = 6;
        runService.Scene.VideoPrompt = "scene 1 visual motion";
        runService.Scene.VideoNegativePrompt = "no sound, no music, no dialogue";
        runService.AddImageAsset(files.CreateAsset(730, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true, sceneId: 11));

        for (var sceneNumber = 2; sceneNumber <= 6; sceneNumber++)
        {
            var sceneId = 10 + sceneNumber;
            runService.Scenes.Add(new FilmScene
            {
                Id = sceneId,
                FilmProjectId = 7,
                SceneNumber = sceneNumber,
                DurationSeconds = 5,
                ImagePrompt = $"image {sceneNumber}",
                ImageNegativePrompt = $"image negative {sceneNumber}",
                VideoPrompt = sceneNumber <= 3 ? $"scene {sceneNumber} visual motion" : string.Empty,
                VideoNegativePrompt = sceneNumber <= 3 ? "no sound, no music, no dialogue" : string.Empty,
                DialogueJson = "[]"
            });
            runService.AddImageAsset(files.CreateAsset(730 + sceneNumber, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true, sceneId: sceneId));
        }

        runService.Checkpoint = new AutonomousProjectCheckpoint
        {
            FilmProjectId = 7,
            ExpectedSceneCount = 6,
            SceneCount = 6,
            HasValidStory = true,
            HasValidCharacters = true,
            FirstMissingVideoPromptSceneNumber = 4,
            FirstMissingSelectedVideoSceneNumber = 1
        };
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                Assert.Equal(4, runService.Scenes.First(scene => string.IsNullOrWhiteSpace(scene.VideoPrompt)).SceneNumber);
                foreach (var scene in runService.Scenes.Where(scene => scene.SceneNumber >= 4))
                {
                    scene.VideoPrompt = $"scene {scene.SceneNumber} repaired visual motion, the figure moves quietly across frame";
                    scene.VideoNegativePrompt = "no sound, no music, no dialogue";
                }
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(0, storyService.GenerateStoryCharactersCallCount);
        Assert.Equal(0, storyService.GenerateNarrativeScenesCallCount);
        Assert.Equal(0, storyService.GenerateImagePromptsCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.All(runService.Scenes, scene => Assert.False(string.IsNullOrWhiteSpace(scene.VideoPrompt)));
        Assert.Contains(AutonomousGenerationRunStatus.GeneratingVideoPrompts, runService.Transitions);
    }

    [Fact]
    public async Task RunAsync_ProjectId16LikeCheckpoint_StartsVideoPromptsAtSceneOne_ThenGeneratesImages()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Pending;
        runService.Run.CurrentStage = AutonomousGenerationStage.Pending;
        runService.Project.CalculatedClipCount = 6;
        runService.Scene.VideoPrompt = string.Empty;
        runService.Scene.VideoNegativePrompt = string.Empty;

        for (var sceneNumber = 2; sceneNumber <= 6; sceneNumber++)
        {
            runService.Scenes.Add(new FilmScene
            {
                Id = 10 + sceneNumber,
                FilmProjectId = 7,
                SceneNumber = sceneNumber,
                DurationSeconds = 5,
                ImagePrompt = $"image {sceneNumber}",
                ImageNegativePrompt = $"image negative {sceneNumber}",
                VideoPrompt = string.Empty,
                VideoNegativePrompt = string.Empty,
                DialogueJson = "[]"
            });
        }

        runService.Checkpoint = new AutonomousProjectCheckpoint
        {
            FilmProjectId = 7,
            ExpectedSceneCount = 6,
            SceneCount = 6,
            HasValidStory = true,
            HasValidCharacters = true,
            FirstMissingVideoPromptSceneNumber = 1,
            FirstMissingSelectedImageSceneNumber = 1,
            FirstMissingSelectedVideoSceneNumber = 1
        };
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                Assert.Equal(1, runService.Scenes.First(scene => string.IsNullOrWhiteSpace(scene.VideoPrompt)).SceneNumber);
                foreach (var scene in runService.Scenes)
                {
                    scene.VideoPrompt = $"scene {scene.SceneNumber} visible motion";
                    scene.VideoNegativePrompt = "no sound, no music, no dialogue";
                }
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(["video-prompts"], storyService.StageCalls);
        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(0, storyService.GenerateStoryCharactersCallCount);
        Assert.Equal(0, storyService.GenerateNarrativeScenesCallCount);
        Assert.Equal(0, storyService.GenerateImagePromptsCallCount);
        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(6, imageService.GenerateCallCount);
        Assert.Equal(6, videoService.GenerateCallCount);
        Assert.Contains(AutonomousGenerationRunStatus.GeneratingVideoPrompts, runService.Transitions);
        Assert.Contains(AutonomousGenerationRunStatus.GeneratingImages, runService.Transitions);
    }

    [Fact]
    public async Task RunAsync_ProjectId17LikeSceneFiveVideoPromptFailure_RecoversAndContinuesToImages()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Pending;
        runService.Run.CurrentStage = AutonomousGenerationStage.Pending;
        runService.Project.CalculatedClipCount = 6;
        runService.Scene.VideoPrompt = "scene 1 existing visual motion";
        runService.Scene.VideoNegativePrompt = "no sound, no music, no dialogue";

        for (var sceneNumber = 2; sceneNumber <= 6; sceneNumber++)
        {
            runService.Scenes.Add(new FilmScene
            {
                Id = 10 + sceneNumber,
                FilmProjectId = 7,
                SceneNumber = sceneNumber,
                DurationSeconds = 5,
                ImagePrompt = $"image {sceneNumber}",
                ImageNegativePrompt = $"image negative {sceneNumber}",
                VideoPrompt = sceneNumber <= 4 ? $"scene {sceneNumber} existing visual motion" : string.Empty,
                VideoNegativePrompt = sceneNumber <= 4 ? "no sound, no music, no dialogue" : string.Empty,
                DialogueJson = "[]"
            });
        }

        var originalPrompts = runService.Scenes
            .Where(scene => scene.SceneNumber <= 4)
            .ToDictionary(scene => scene.SceneNumber, scene => scene.VideoPrompt);
        runService.Checkpoint = new AutonomousProjectCheckpoint
        {
            FilmProjectId = 7,
            ExpectedSceneCount = 6,
            SceneCount = 6,
            HasValidStory = true,
            HasValidCharacters = true,
            FirstMissingVideoPromptSceneNumber = 5,
            FirstMissingSelectedImageSceneNumber = 1,
            FirstMissingSelectedVideoSceneNumber = 1
        };
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                Assert.Equal(5, runService.Scenes.First(scene => string.IsNullOrWhiteSpace(scene.VideoPrompt)).SceneNumber);
                runService.Scenes.Single(scene => scene.SceneNumber == 5).VideoPrompt = "scene 5 fallback visible motion";
                runService.Scenes.Single(scene => scene.SceneNumber == 5).VideoNegativePrompt = "no sound, no music, no dialogue";
                runService.Scenes.Single(scene => scene.SceneNumber == 6).VideoPrompt = "scene 6 visible motion";
                runService.Scenes.Single(scene => scene.SceneNumber == 6).VideoNegativePrompt = "no sound, no music, no dialogue";
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(0, storyService.GenerateStoryCharactersCallCount);
        Assert.Equal(0, storyService.GenerateNarrativeScenesCallCount);
        Assert.Equal(0, storyService.GenerateImagePromptsCallCount);
        foreach (var scene in runService.Scenes.Where(scene => scene.SceneNumber <= 4))
        {
            Assert.Equal(originalPrompts[scene.SceneNumber], scene.VideoPrompt);
        }

        Assert.Equal("scene 5 fallback visible motion", runService.Scenes.Single(scene => scene.SceneNumber == 5).VideoPrompt);
        Assert.Equal("scene 6 visible motion", runService.Scenes.Single(scene => scene.SceneNumber == 6).VideoPrompt);
        Assert.Equal(6, imageService.GenerateCallCount);
        Assert.Contains(AutonomousGenerationRunStatus.GeneratingImages, runService.Transitions);
    }

    [Fact]
    public async Task RunAsync_DoesNotStartImageGeneration_BeforeAllVideoPromptsExist()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingImages;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingImages;
        runService.Scene.VideoPrompt = string.Empty;
        runService.Scene.VideoNegativePrompt = string.Empty;
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(AutonomousGenerationRunStatus.Failed, runService.Run.Status);
        Assert.Contains("Video generation cannot start before every video prompt", runService.Run.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AllCompletedImageWorkItems_TransitionToVideoPromptGeneration()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingImages;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingImages;
        runService.WorkItem.ImageStatus = AutonomousWorkItemStatus.Completed;
        runService.Scene.VideoPrompt = string.Empty;
        runService.SelectedImageAsset = files.CreateAsset(635, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        var storyService = new FakeStoryGenerationService
        {
            GenerateVideoPrompts = _ =>
            {
                runService.Scene.VideoPrompt = "video prompt";
                runService.Scene.VideoNegativePrompt = "video negative";
            }
        };
        var orchestrator = CreateOrchestrator(runService, storyService, new FakeImageGenerationService(runService, files), new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Contains(AutonomousGenerationRunStatus.GeneratingVideoPrompts, runService.Transitions);
        Assert.Equal(1, storyService.GenerateVideoPromptsCallCount);
    }

    [Fact]
    public async Task RunAsync_VideoGenerationRequestUsesSelectedImageFileReference()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        var selectedImage = files.CreateAsset(636, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        runService.SelectedImageAsset = selectedImage;
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, new FakeStoryGenerationService(), new FakeImageGenerationService(runService, files), videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(selectedImage.Id, videoService.LastRequest?.SourceImageAssetId);
        Assert.Equal(selectedImage.FilePath, videoService.LastRequest?.SourceImagePath);
        Assert.NotEqual(runService.Scene.ImagePrompt, videoService.LastRequest?.SourceImagePath);
    }

    [Fact]
    public async Task RunAsync_TransientWanGpGenerateVideoToolFailure_RetriesCurrentSceneWithoutRegeneratingImages()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        var selectedImage = files.CreateAsset(636, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        runService.SelectedImageAsset = selectedImage;
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files)
        {
            FailuresBeforeSuccess = 1
        };
        var runtime = new FakeWanGpRuntimeCoordinator();
        var orchestrator = CreateOrchestrator(
            runService,
            new FakeStoryGenerationService(),
            imageService,
            videoService,
            runtimeCoordinator: runtime);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(2, videoService.GenerateCallCount);
        Assert.True(runtime.EnsureReadyCallCount >= 2);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.VideoStatus);
        Assert.NotNull(runService.WorkItem.VideoMediaAssetId);
        Assert.Equal(2, runService.WorkItem.VideoAttemptCount);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.True(string.IsNullOrWhiteSpace(runService.Run.LastError));
    }

    [Fact]
    public async Task RunAsync_WanGpBusyVideoSubmit_ReconcilesProducedVideoBeforeRetryingSubmit()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        runService.SelectedImageAsset = files.CreateAsset(637, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        var videoService = new FakeVideoGenerationService(runService, files)
        {
            BusyFailureBeforeSuccess = true,
            AddVideoAssetAfterFailure = true
        };
        var orchestrator = CreateOrchestrator(
            runService,
            new FakeStoryGenerationService(),
            new FakeImageGenerationService(runService, files),
            videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(1, videoService.GenerateCallCount);
        Assert.Equal(AutonomousWorkItemStatus.Completed, runService.WorkItem.VideoStatus);
        Assert.NotNull(runService.WorkItem.VideoMediaAssetId);
        Assert.Equal(1, runService.WorkItem.VideoAttemptCount);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.True(string.IsNullOrWhiteSpace(runService.Run.LastError));
    }

    [Fact]
    public async Task RunAsync_ImageGenerationUsesPreviousSceneSelectedImageReference()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Project.CalculatedClipCount = 3;
        runService.Scenes.Add(new FilmScene
        {
            Id = 12,
            FilmProjectId = 7,
            SceneNumber = 2,
            DurationSeconds = 5,
            ImagePrompt = "image 2",
            ImageNegativePrompt = "image negative 2",
            VideoPrompt = "video 2",
            VideoNegativePrompt = "video negative 2",
            DialogueJson = "[]"
        });
        runService.Scenes.Add(new FilmScene
        {
            Id = 13,
            FilmProjectId = 7,
            SceneNumber = 3,
            DurationSeconds = 5,
            ImagePrompt = "image 3",
            ImageNegativePrompt = "image negative 3",
            VideoPrompt = "video 3",
            VideoNegativePrompt = "video negative 3",
            DialogueJson = "[]"
        });
        await runService.EnsureSceneWorkItemsAsync(runService.Run.Id);
        var imageService = new FakeImageGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, new FakeStoryGenerationService(), imageService, new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(3, imageService.Requests.Count);
        Assert.Null(imageService.Requests[0].Request.SourceImageAssetId);
        Assert.Equal(runService.ImageAssets.Single(asset => asset.SceneId == 11).Id, imageService.Requests[1].Request.SourceImageAssetId);
        Assert.Equal(runService.ImageAssets.Single(asset => asset.SceneId == 11).FilePath, imageService.Requests[1].Request.SourceImagePath);
        Assert.Equal(runService.ImageAssets.Single(asset => asset.SceneId == 12).Id, imageService.Requests[2].Request.SourceImageAssetId);
        Assert.Equal(runService.ImageAssets.Single(asset => asset.SceneId == 12).FilePath, imageService.Requests[2].Request.SourceImagePath);
    }

    [Fact]
    public async Task RunAsync_ImageGenerationStops_WhenPreviousSelectedImageReferenceIsMissing()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Scenes.Add(new FilmScene
        {
            Id = 12,
            FilmProjectId = 7,
            SceneNumber = 2,
            DurationSeconds = 5,
            ImagePrompt = "image 2",
            ImageNegativePrompt = "image negative 2",
            VideoPrompt = "video 2",
            VideoNegativePrompt = "video negative 2",
            DialogueJson = "[]"
        });
        await runService.EnsureSceneWorkItemsAsync(runService.Run.Id);
        var imageService = new FakeImageGenerationService(runService, files)
        {
            SuppressAssetSave = true
        };
        var orchestrator = CreateOrchestrator(runService, new FakeStoryGenerationService(), imageService, new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(2, imageService.GenerateCallCount);
        Assert.Equal(AutonomousGenerationRunStatus.Failed, runService.Run.Status);
        Assert.Contains("ge", runService.Run.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_DoesNotStartVideoGeneration_BeforeAllVideoPromptsExist()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        runService.Scene.VideoPrompt = string.Empty;
        runService.SelectedImageAsset = files.CreateAsset(640, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, new FakeStoryGenerationService(), new FakeImageGenerationService(runService, files), videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, videoService.GenerateCallCount);
        Assert.Equal(AutonomousGenerationRunStatus.Failed, runService.Run.Status);
        Assert.Contains("Video generation cannot start before every video prompt", runService.Run.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PausedRunDoesNotStartServices()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Paused;
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(AutonomousGenerationRunStatus.Paused, runService.Run.Status);
        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(0, videoService.GenerateCallCount);
    }

    [Fact]
    public async Task RunAsync_CancelRequestedRunMarksCancelled()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.CancellationRequested = true;
        var storyService = new FakeStoryGenerationService();
        var orchestrator = CreateOrchestrator(runService, storyService, new FakeImageGenerationService(runService, files), new FakeVideoGenerationService(runService, files));

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(AutonomousGenerationRunStatus.Cancelled, runService.Run.Status);
        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
    }

    [Fact]
    public async Task RunAsync_ResumeFromGeneratingVideos_SkipsStoryAndCompletedImages()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        runService.Run.AttemptCount = 4;
        runService.WorkItem.ImageStatus = AutonomousWorkItemStatus.Completed;
        runService.SelectedImageAsset = files.CreateAsset(701, MediaType.Image, MediaAssetRole.ReferenceImage, selected: true);
        var storyService = new FakeStoryGenerationService();
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await orchestrator.RunAsync(runService.Run.Id);

        Assert.Equal(0, storyService.GenerateStoryNarrativeCallCount);
        Assert.Equal(0, imageService.GenerateCallCount);
        Assert.Equal(1, videoService.GenerateCallCount);
        Assert.Equal(4, runService.Run.AttemptCount);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task RetryFailedRun_WithExistingStoryAndNoScenes_ReusesRunAndCreatesSingleScene()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.Failed;
        runService.Run.CurrentStage = AutonomousGenerationStage.Failed;
        runService.Run.LastError = "Sahne 1 icin model cevabi dogrulanamadi.";
        runService.Scenes.Clear();
        runService.WorkItems.Clear();
        var originalRunId = runService.Run.Id;
        var storyService = new FakeStoryGenerationService
        {
            ExistingStoryCount = 1,
            CreateMissingScenes = projectId =>
            {
                if (runService.Scenes.Count == 0)
                {
                    runService.Scenes.Add(new FilmScene
                    {
                        Id = 111,
                        FilmProjectId = projectId,
                        SceneNumber = 1,
                        DurationSeconds = 5,
                        ImagePrompt = "image",
                        ImageNegativePrompt = "image negative",
                        VideoPrompt = "video",
                        VideoNegativePrompt = "video negative",
                        DialogueJson = "[]"
                    });
                }
            }
        };
        var imageService = new FakeImageGenerationService(runService, files);
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(runService, storyService, imageService, videoService);

        await runService.RetryAsync(originalRunId);
        await orchestrator.RunAsync(originalRunId);

        Assert.Equal(originalRunId, runService.Run.Id);
        Assert.Equal(1, storyService.ExistingStoryCount);
        Assert.Equal(0, storyService.StoryRegenerationCallCount);
        Assert.Equal(1, storyService.GenerateNarrativeScenesCallCount);
        Assert.Single(runService.Scenes);
        Assert.Single(runService.WorkItems);
        Assert.Equal(1, runService.Scenes[0].SceneNumber);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
    }

    [Fact]
    public async Task TryClaimRunAsync_RejectsFreshOtherWorker_AndAcceptsStaleHeartbeat()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        runService.Run.Status = AutonomousGenerationRunStatus.GeneratingVideos;
        runService.Run.CurrentStage = AutonomousGenerationStage.GeneratingVideos;
        runService.Run.WorkerId = "old-worker";
        runService.Run.LastHeartbeatAtUtc = DateTime.UtcNow;

        var freshClaim = await runService.TryClaimRunAsync(
            runService.Run.Id,
            "new-worker",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15));
        Assert.False(freshClaim);
        Assert.Equal("old-worker", runService.Run.WorkerId);

        runService.Run.LastHeartbeatAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(11);
        var staleClaim = await runService.TryClaimRunAsync(
            runService.Run.Id,
            "new-worker",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15));

        Assert.True(staleClaim);
        Assert.Equal("new-worker", runService.Run.WorkerId);
        Assert.Equal(AutonomousGenerationRunStatus.GeneratingVideos, runService.Run.Status);
        Assert.Equal(AutonomousGenerationStage.GeneratingVideos, runService.Run.CurrentStage);
    }

    [Fact]
    public async Task RunAsync_RenewsLeaseDuringLongImageGeneration_AndBlocksSecondWorkerClaim()
    {
        using var files = new TemporaryMediaFiles();
        var runService = FakeAutonomousRunService.Create(files);
        Assert.True(await runService.TryClaimRunAsync(
            runService.Run.Id,
            "worker-1",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5)));
        var imageService = new FakeImageGenerationService(runService, files)
        {
            DelayBeforeCompleting = TimeSpan.FromSeconds(8)
        };
        var videoService = new FakeVideoGenerationService(runService, files);
        var orchestrator = CreateOrchestrator(
            runService,
            new FakeStoryGenerationService(),
            imageService,
            videoService,
            new AutonomousGenerationOptions
            {
                HeartbeatIntervalSeconds = 1,
                LeaseExtensionSeconds = 5,
                StaleHeartbeatSeconds = 2
            });

        var runTask = orchestrator.RunAsync(runService.Run.Id, "worker-1");
        await imageService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(3));

        var secondWorkerClaim = await runService.TryClaimRunAsync(
            runService.Run.Id,
            "worker-2",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));

        await runTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(secondWorkerClaim);
        Assert.Equal(1, imageService.GenerateCallCount);
        Assert.Equal(AutonomousGenerationRunStatus.Completed, runService.Run.Status);
        Assert.Null(runService.Run.WorkerId);
    }

    [Fact]
    public async Task RetryPolicy_RetriesUntilSuccess()
    {
        var policy = new AutonomousGenerationRetryPolicy(maxAttempts: 3);
        var attempts = 0;

        await policy.ExecuteAsync((_, _) =>
        {
            attempts++;
            if (attempts < 3)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.CompletedTask;
        });

        Assert.Equal(3, attempts);
    }

    private static AutonomousGenerationOrchestrator CreateOrchestrator(
        FakeAutonomousRunService runService,
        FakeStoryGenerationService storyService,
        FakeImageGenerationService imageService,
        FakeVideoGenerationService videoService,
        AutonomousGenerationOptions? options = null,
        FakeWanGpRuntimeCoordinator? runtimeCoordinator = null) =>
        new(
            runService,
            storyService,
            imageService,
            videoService,
            new FakeAudioGenerationService(),
            new FakeVideoGenerationRequestFactory(),
            new FakeFinalMovieAssemblyService(),
            runtimeCoordinator ?? new FakeWanGpRuntimeCoordinator(),
            new AutonomousGenerationRetryPolicy(maxAttempts: 2),
            Microsoft.Extensions.Options.Options.Create(options ?? new AutonomousGenerationOptions
            {
                HeartbeatIntervalSeconds = 1,
                LeaseExtensionSeconds = 60,
                StaleHeartbeatSeconds = 30,
                MediaRetryDelaySeconds = 0
            }),
            NullLogger<AutonomousGenerationOrchestrator>.Instance);

    private sealed class FakeAutonomousRunService : IAutonomousGenerationRunService
    {
        private readonly TemporaryMediaFiles _files;

        private FakeAutonomousRunService(TemporaryMediaFiles files)
        {
            _files = files;
            Run = new AutonomousGenerationRun
            {
                Id = 101,
                FilmProjectId = 7,
                Status = AutonomousGenerationRunStatus.Pending,
                CurrentStage = AutonomousGenerationStage.Pending,
                StartedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                LastHeartbeatAtUtc = DateTime.UtcNow,
                ConfigurationSnapshotJson = System.Text.Json.JsonSerializer.Serialize(new AutonomousGenerationConfigurationSnapshot
                {
                    FilmProjectId = 7,
                    ProjectName = "Auto",
                    CalculatedClipCount = 1,
                    ClipDurationSeconds = 5,
                    Resolution = "1024x1024",
                    ImageModelType = "qwen_image_20B",
                    VideoModelType = "ltx2_22B_distilled_gguf_q4_k_m",
                    GenerateAudio = false,
                    PreferLtxNativeDialogue = false
                })
            };
            Project = new FilmProject
            {
                Id = 7,
                ProjectName = "Auto",
                Subject = "subject",
                CalculatedClipCount = 1,
                ClipDurationSeconds = 5,
                Resolution = "1024x1024"
            };
            Scenes.Add(new FilmScene
            {
                Id = 11,
                FilmProjectId = 7,
                SceneNumber = 1,
                DurationSeconds = 5,
                ImagePrompt = "image",
                ImageNegativePrompt = "image negative",
                VideoPrompt = "video",
                VideoNegativePrompt = "video negative",
                DialogueJson = "[]"
            });
            WorkItems.Add(new AutonomousSceneWorkItem
            {
                Id = 301,
                AutonomousGenerationRunId = 101,
                StorySceneId = 11,
                SceneNumber = 1
            });
        }

        public AutonomousGenerationRun Run { get; }
        public FilmProject Project { get; }
        public FilmScene Scene => Scenes.Single();
        public AutonomousSceneWorkItem WorkItem => WorkItems.Single();
        public List<FilmScene> Scenes { get; } = [];
        public List<AutonomousSceneWorkItem> WorkItems { get; } = [];
        public List<SceneMediaAsset> ImageAssets { get; } = [];
        public List<SceneMediaAsset> VideoAssets { get; } = [];
        public List<AutonomousGenerationRunStatus> Transitions { get; } = [];
        public AutonomousProjectCheckpoint Checkpoint { get; set; } = new()
        {
            FilmProjectId = 7,
            ExpectedSceneCount = 1,
            SceneCount = 0,
            HasValidStory = false,
            HasValidCharacters = false
        };
        public SceneMediaAsset? SelectedImageAsset
        {
            get => ImageAssets.FirstOrDefault(asset => asset.IsSelected && IsValidAsset(asset));
            set
            {
                ImageAssets.Clear();
                if (value is not null)
                {
                    ImageAssets.Add(value);
                }
            }
        }
        public SceneMediaAsset? SelectedVideoAsset
        {
            get => VideoAssets.FirstOrDefault(asset => asset.IsSelected && IsValidAsset(asset));
            set
            {
                VideoAssets.Clear();
                if (value is not null)
                {
                    VideoAssets.Add(value);
                }
            }
        }

        public static FakeAutonomousRunService Create(TemporaryMediaFiles files) => new(files);

        public Task<AutonomousGenerationRunSummary> StartOrGetActiveRunAsync(int filmProjectId, AutonomousGenerationConfigurationSnapshot snapshot, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AutonomousGenerationRun?> GetRunAsync(int runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AutonomousGenerationRun?>(Run);

        public Task<AutonomousGenerationRunSummary?> GetLatestRunForProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AutonomousGenerationRunSummary?>(null);

        public Task<AutonomousProjectCheckpoint> GetProjectCheckpointAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Checkpoint);

        public Task<IReadOnlyList<AutonomousGenerationRunSummary>> GetRunnableRunsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AutonomousGenerationRunSummary>>(Array.Empty<AutonomousGenerationRunSummary>());

        public Task<bool> TryClaimRunAsync(int runId, string workerId, TimeSpan staleHeartbeatThreshold, TimeSpan leaseExtension, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            if (Run.WorkerId is not null &&
                !string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal) &&
                Run.LastHeartbeatAtUtc >= now - staleHeartbeatThreshold)
            {
                return Task.FromResult(false);
            }

            Run.WorkerId = workerId;
            Run.LeaseExpiresAtUtc = now + leaseExtension;
            Run.LastHeartbeatAtUtc = now;
            return Task.FromResult(true);
        }

        public Task<bool> TryRenewLeaseAsync(int runId, string workerId, TimeSpan leaseExtension, string message, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Run.LeaseExpiresAtUtc = DateTime.UtcNow + leaseExtension;
            Run.LastHeartbeatAtUtc = DateTime.UtcNow;
            Run.LastMessage = message;
            return Task.FromResult(true);
        }

        public Task<bool> IsRunOwnedByWorkerAsync(int runId, string workerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(Run.WorkerId) || string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal));

        public Task ReleaseClaimAsync(int runId, string workerId, CancellationToken cancellationToken = default)
        {
            if (string.Equals(Run.WorkerId, workerId, StringComparison.Ordinal))
            {
                Run.WorkerId = null;
                Run.LeaseExpiresAtUtc = null;
            }

            return Task.CompletedTask;
        }

        public Task<FilmProject> GetProjectAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project);

        public Task<IReadOnlyList<FilmScene>> GetScenesAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FilmScene>>(Scenes.ToList());

        public Task<IReadOnlyList<AutonomousSceneWorkItem>> EnsureSceneWorkItemsAsync(int runId, CancellationToken cancellationToken = default)
        {
            foreach (var scene in Scenes.Where(scene => WorkItems.All(item => item.StorySceneId != scene.Id)))
            {
                WorkItems.Add(new AutonomousSceneWorkItem
                {
                    Id = 301 + WorkItems.Count,
                    AutonomousGenerationRunId = runId,
                    StorySceneId = scene.Id,
                    SceneNumber = scene.SceneNumber,
                    ImageStatus = AutonomousWorkItemStatus.Pending,
                    VideoStatus = AutonomousWorkItemStatus.Pending,
                    AudioStatus = AutonomousWorkItemStatus.Pending,
                    FinalizationStatus = AutonomousWorkItemStatus.Pending
                });
            }

            Run.TotalSceneCount = Scenes.Count;
            foreach (var item in WorkItems)
            {
                var selectedImage = ImageAssets.FirstOrDefault(asset =>
                    asset.SceneId == item.StorySceneId &&
                    asset.MediaType == MediaType.Image &&
                    asset.IsSelected &&
                    IsValidAsset(asset));
                if (selectedImage is not null)
                {
                    item.ImageStatus = AutonomousWorkItemStatus.Completed;
                    item.ImageMediaAssetId = selectedImage.Id;
                }

                var selectedVideo = VideoAssets.FirstOrDefault(asset =>
                    asset.SceneId == item.StorySceneId &&
                    asset.MediaType == MediaType.Video &&
                    asset.IsSelected &&
                    IsValidAsset(asset));
                if (selectedVideo is not null)
                {
                    item.VideoStatus = AutonomousWorkItemStatus.Completed;
                    item.VideoMediaAssetId = selectedVideo.Id;
                }
            }

            return Task.FromResult<IReadOnlyList<AutonomousSceneWorkItem>>(WorkItems.OrderBy(item => item.SceneNumber).ToList());
        }

        public Task<SceneMediaAsset?> FindValidImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SceneMediaAsset?>(ImageAssets
                .Where(asset => asset.SceneId == sceneId && asset.MediaType == MediaType.Image)
                .OrderByDescending(asset => asset.IsSelected)
                .ThenByDescending(asset => asset.CreatedAt)
                .FirstOrDefault(IsValidAsset));

        public Task<SceneMediaAsset?> FindValidSelectedImageAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SceneMediaAsset?>(ImageAssets
                .Where(asset => asset.SceneId == sceneId && asset.MediaType == MediaType.Image && asset.IsSelected)
                .OrderByDescending(asset => asset.CreatedAt)
                .FirstOrDefault(IsValidAsset));

        public Task<SceneMediaAsset?> FindValidSelectedVideoAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SceneMediaAsset?>(VideoAssets
                .Where(asset => asset.SceneId == sceneId && asset.MediaType == MediaType.Video && asset.IsSelected)
                .OrderByDescending(asset => asset.CreatedAt)
                .FirstOrDefault(IsValidAsset));

        public Task<SceneMediaAsset?> FindValidSceneAudioAssetAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SceneMediaAsset?>(null);

        public Task<bool> HasActiveGenerationJobAsync(int sceneId, MediaType mediaType, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<SceneSpeechSegment>> GetSpeechSegmentsAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SceneSpeechSegment>>(Array.Empty<SceneSpeechSegment>());

        public Task MarkHeartbeatAsync(int runId, string message, double? overallProgressPercentage = null, CancellationToken cancellationToken = default)
        {
            Run.LastMessage = message;
            if (overallProgressPercentage is not null)
            {
                Run.OverallProgressPercentage = overallProgressPercentage.Value;
            }

            return Task.CompletedTask;
        }

        public Task TransitionAsync(int runId, AutonomousGenerationRunStatus status, string message, CancellationToken cancellationToken = default)
        {
            var stateMachine = new AutonomousGenerationStateMachine();
            if (!stateMachine.CanTransition(Run.Status, status))
            {
                throw new InvalidOperationException($"Invalid transition: {Run.Status} -> {status}");
            }

            Run.Status = status;
            Run.CurrentStage = status switch
            {
                AutonomousGenerationRunStatus.Completed => AutonomousGenerationStage.Completed,
                AutonomousGenerationRunStatus.Cancelled => AutonomousGenerationStage.Cancelled,
                _ => (AutonomousGenerationStage)(int)status
            };
            Run.LastMessage = message;
            Transitions.Add(status);
            return Task.CompletedTask;
        }

        public Task SetCurrentSceneAsync(int runId, int? sceneId, int? sceneNumber, CancellationToken cancellationToken = default)
        {
            Run.CurrentSceneId = sceneId;
            Run.CurrentSceneNumber = sceneNumber;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemImageAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default)
        {
            var item = WorkItems.Single(workItem => workItem.Id == workItemId);
            item.ImageStatus = status;
            item.ImageMediaAssetId = mediaAssetId ?? item.ImageMediaAssetId;
            if (incrementAttempt) item.ImageAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemVideoAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default)
        {
            var item = WorkItems.Single(workItem => workItem.Id == workItemId);
            item.VideoStatus = status;
            item.VideoMediaAssetId = mediaAssetId ?? item.VideoMediaAssetId;
            if (incrementAttempt) item.VideoAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemAudioAsync(int workItemId, AutonomousWorkItemStatus status, int? mediaAssetId, string? error, bool incrementAttempt, CancellationToken cancellationToken = default)
        {
            var item = WorkItems.Single(workItem => workItem.Id == workItemId);
            item.AudioStatus = status;
            item.AudioMediaAssetId = mediaAssetId ?? item.AudioMediaAssetId;
            if (incrementAttempt) item.AudioAttemptCount++;
            return Task.CompletedTask;
        }

        public Task MarkWorkItemFinalizationAsync(int workItemId, AutonomousWorkItemStatus status, string? error, CancellationToken cancellationToken = default)
        {
            WorkItems.Single(workItem => workItem.Id == workItemId).FinalizationStatus = status;
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(int runId, string message, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Completed;
            Run.CurrentStage = AutonomousGenerationStage.Completed;
            Run.LastMessage = message;
            Run.OverallProgressPercentage = 100;
            return Task.CompletedTask;
        }

        public Task FailRunAsync(int runId, string error, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Failed;
            Run.LastError = error;
            return Task.CompletedTask;
        }

        public Task RequestCancellationAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.CancellationRequested = true;
            Run.Status = AutonomousGenerationRunStatus.CancelRequested;
            return Task.CompletedTask;
        }

        public Task PauseAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Paused;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Pending;
            return Task.CompletedTask;
        }

        public Task RetryAsync(int runId, CancellationToken cancellationToken = default)
        {
            Run.Status = AutonomousGenerationRunStatus.Pending;
            Run.CurrentStage = AutonomousGenerationStage.Pending;
            Run.LastError = string.Empty;
            Run.AttemptCount++;
            return Task.CompletedTask;
        }

        public void SelectImageAsset(int assetId)
        {
            var asset = ImageAssets.Single(item => item.Id == assetId);
            foreach (var image in ImageAssets.Where(item => item.SceneId == asset.SceneId && item.MediaType == MediaType.Image))
            {
                image.IsSelected = image.Id == assetId;
            }
        }

        public void AddImageAsset(SceneMediaAsset asset)
        {
            if (asset.IsSelected)
            {
                foreach (var image in ImageAssets.Where(item => item.SceneId == asset.SceneId && item.MediaType == MediaType.Image))
                {
                    image.IsSelected = false;
                }
            }

            ImageAssets.Add(asset);
        }

        public void AddVideoAsset(SceneMediaAsset asset)
        {
            if (asset.IsSelected)
            {
                foreach (var video in VideoAssets.Where(item => item.SceneId == asset.SceneId && item.MediaType == MediaType.Video))
                {
                    video.IsSelected = false;
                }
            }

            VideoAssets.Add(asset);
        }

        private static bool IsValidAsset(SceneMediaAsset asset) =>
            !string.IsNullOrWhiteSpace(asset.FilePath) &&
            File.Exists(asset.FilePath) &&
            new FileInfo(asset.FilePath).Length > 0;
    }

    private sealed class FakeStoryGenerationService : IStoryGenerationService
    {
        public int GenerateMissingScenesCallCount { get; private set; }
        public int GenerateStoryNarrativeCallCount { get; private set; }
        public int GenerateStoryCharactersCallCount { get; private set; }
        public int GenerateNarrativeScenesCallCount { get; private set; }
        public int GenerateImagePromptsCallCount { get; private set; }
        public int GenerateVideoPromptsCallCount { get; private set; }
        public int ExistingStoryCount { get; set; }
        public int StoryRegenerationCallCount { get; private set; }
        public Action<int>? CreateMissingScenes { get; set; }
        public Action<int>? GenerateVideoPrompts { get; set; }
        public List<string> StageCalls { get; } = [];

        public Task<StoryGenerationProgressResult> GenerateStoryNarrativeAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateStoryNarrativeCallCount++;
            StageCalls.Add("story-narrative");
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 0 });
        }

        public Task<StoryGenerationProgressResult> GenerateStoryCharactersAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateStoryCharactersCallCount++;
            StageCalls.Add("characters");
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 0 });
        }

        public Task<StoryGenerationProgressResult> GenerateAllMissingNarrativeScenesAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateNarrativeScenesCallCount++;
            StageCalls.Add("narrative-scenes");
            CreateMissingScenes?.Invoke(filmProjectId);
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 1 });
        }

        public Task<StoryGenerationProgressResult> GenerateAllMissingImagePromptsAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateImagePromptsCallCount++;
            StageCalls.Add("image-prompts");
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 1 });
        }

        public Task<StoryGenerationProgressResult> GenerateAllMissingVideoPromptsAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateVideoPromptsCallCount++;
            StageCalls.Add("video-prompts");
            GenerateVideoPrompts?.Invoke(filmProjectId);
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 1 });
        }

        public Task<StoryGenerationProgressResult> GenerateStoryAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            StoryRegenerationCallCount++;
            return GenerateAllMissingScenesAsync(filmProjectId, progress, cancellationToken);
        }

        public Task<StoryGenerationProgressResult> GenerateAllMissingScenesAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateMissingScenesCallCount++;
            CreateMissingScenes?.Invoke(filmProjectId);
            return Task.FromResult(new StoryGenerationProgressResult { FilmProjectId = filmProjectId, GeneratedSceneCount = 1 });
        }

        public Task<StoryGenerationProgressResult> GenerateNextMissingSceneAsync(int filmProjectId, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateAllMissingScenesAsync(filmProjectId, progress, cancellationToken);

        public Task<StoryGenerationProgressResult> GenerateUpToMissingScenesAsync(int filmProjectId, int maximumSceneCount, IProgress<StoryGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            GenerateAllMissingScenesAsync(filmProjectId, progress, cancellationToken);
    }

    private sealed class FakeWanGpRuntimeCoordinator : IWanGpRuntimeCoordinator
    {
        public int EnsureReadyCallCount { get; private set; }
        public WanGpRuntimeStatus LastStatus { get; private set; } = Ready();

        public Task<WanGpRuntimeStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            EnsureReadyCallCount++;
            LastStatus = Ready();
            return Task.FromResult(LastStatus);
        }

        public Task<WanGpRuntimeStatus> RefreshStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(LastStatus);

        public Task StopOwnedProcessAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static WanGpRuntimeStatus Ready() => new()
        {
            IsReady = true,
            McpState = WanGpMcpConnectionState.Connected,
            GuiState = WanGpGuiState.Open,
            Message = "ready"
        };
    }

    private sealed class FakeImageGenerationService : IImageGenerationService
    {
        private readonly FakeAutonomousRunService _runService;
        private readonly TemporaryMediaFiles _files;

        public FakeImageGenerationService(FakeAutonomousRunService runService, TemporaryMediaFiles files)
        {
            _runService = runService;
            _files = files;
        }

        public int GenerateCallCount { get; private set; }
        public WanGpImageGenerationRequest? LastRequest { get; private set; }
        public List<(int SceneId, WanGpImageGenerationRequest Request)> Requests { get; } = [];
        public TimeSpan DelayBeforeCompleting { get; set; }
        public bool SuppressAssetSave { get; set; }
        public int FailuresBeforeSuccess { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GenerationJob> GenerateSceneImageAsync(int sceneId, WanGpImageGenerationRequest request, IProgress<MediaGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateCallCount++;
            LastRequest = request;
            Requests.Add((sceneId, request));
            Started.TrySetResult();
            if (DelayBeforeCompleting > TimeSpan.Zero)
            {
                await Task.Delay(DelayBeforeCompleting, cancellationToken);
            }

            if (GenerateCallCount <= FailuresBeforeSuccess)
            {
                throw new InvalidOperationException("Hedef makine etkin olarak reddettiğinden bağlantı kurulamadı. (127.0.0.1:7866)");
            }

            if (!SuppressAssetSave)
            {
                _runService.AddImageAsset(_files.CreateAsset(401 + GenerateCallCount, MediaType.Image, MediaAssetRole.ReferenceImage, request.AutoSelectOutput, sceneId: sceneId));
            }

            return new GenerationJob { Id = 21, SceneId = sceneId, Status = GenerationJobStatus.Completed };
        }

        public Task GenerateMissingImagesAsync(int filmProjectId, WanGpImageGenerationRequest templateRequest, bool stopOnError, IProgress<MediaGenerationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelActiveJobAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSelectedAssetAsync(int assetId, CancellationToken cancellationToken = default)
        {
            _runService.SelectImageAsset(assetId);
            return Task.CompletedTask;
        }
        public Task<SceneMediaAsset> ImportExistingWanGpOutputAsync(int sceneId, string sourcePath, bool makeSelected = true, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkOrphanRunningJobsInterruptedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeVideoGenerationService : IVideoGenerationService
    {
        private readonly FakeAutonomousRunService _runService;
        private readonly TemporaryMediaFiles _files;

        public FakeVideoGenerationService(FakeAutonomousRunService runService, TemporaryMediaFiles files)
        {
            _runService = runService;
            _files = files;
        }

        public int GenerateCallCount { get; private set; }
        public WanGpVideoGenerationRequest? LastRequest { get; private set; }
        public int FailuresBeforeSuccess { get; set; }
        public bool BusyFailureBeforeSuccess { get; set; }
        public bool AddVideoAssetAfterFailure { get; set; }

        public Task<GenerationJob> GenerateSceneVideoAsync(WanGpVideoGenerationRequest request, IProgress<MediaGenerationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            GenerateCallCount++;
            LastRequest = request;
            if (BusyFailureBeforeSuccess && GenerateCallCount == 1)
            {
                if (AddVideoAssetAfterFailure)
                {
                    _runService.AddVideoAsset(_files.CreateAsset(550 + GenerateCallCount, MediaType.Video, MediaAssetRole.GeneratedSilentVideo, selected: true, sceneId: request.SceneId));
                }

                throw new WanGpToolExecutionException("wangp_generate", "Error executing tool wangp_generate: WanGP session already has a generation in progress");
            }

            if (GenerateCallCount <= FailuresBeforeSuccess)
            {
                throw new WanGpToolExecutionException("wangp_generate", "simulated transient video submit failure");
            }

            _runService.AddVideoAsset(_files.CreateAsset(501 + GenerateCallCount, MediaType.Video, MediaAssetRole.GeneratedSilentVideo, selected: true, sceneId: request.SceneId));
            return Task.FromResult(new GenerationJob { Id = 31, SceneId = request.SceneId, Status = GenerationJobStatus.Completed });
        }

        public Task CancelActiveJobAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetSelectedVideoAssetAsync(int assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeVideoGenerationRequestFactory : IVideoGenerationRequestFactory
    {
        public Task<WanGpVideoGenerationRequest> CreateAsync(VideoGenerationRequestFactoryInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WanGpVideoGenerationRequest
            {
                FilmProjectId = input.FilmProjectId,
                SceneId = input.Scene.Id,
                SceneNumber = input.Scene.SceneNumber,
                SourceImageAssetId = input.SourceImageAsset.Id,
                SourceImagePath = input.SourceImageAsset.FilePath,
                ModelType = input.ModelType,
                Prompt = input.Scene.VideoPrompt,
                Resolution = input.Resolution,
                DurationSeconds = input.Scene.DurationSeconds,
                InferenceSteps = input.InferenceSteps,
                StopOnFailure = true
            });
    }

    private sealed class FakeAudioGenerationService : IAudioGenerationService
    {
        public Task<AudioModelDiscoveryResult> DiscoverKugelAudioAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AudioModelDiscoveryResult());

        public Task<SceneSpeechPlan> CreateBasicSpeechPlanAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneSpeechPlan { Id = 1, SceneId = sceneId });

        public Task<SceneMediaAsset> GenerateSpeechSegmentAsync(int speechSegmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneMediaAsset { Id = speechSegmentId, MediaType = MediaType.Audio });

        public Task<SceneMediaAsset> CreateSpeechTrackForSceneAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneMediaAsset { Id = 701, SceneId = sceneId, MediaType = MediaType.Audio });

        public Task<SceneMediaAsset> CreateFinalDialogueVideoForSceneAsync(int sceneId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SceneMediaAsset { Id = 801, SceneId = sceneId, MediaType = MediaType.Video });
    }

    private sealed class FakeFinalMovieAssemblyService : IFinalMovieAssemblyService
    {
        public Task<string> AssembleLtxNativeDialogueMovieAsync(int filmProjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);
    }

    private sealed class TemporaryMediaFiles : IDisposable
    {
        private readonly List<string> _paths = [];

        public SceneMediaAsset CreateAsset(int id, MediaType mediaType, MediaAssetRole role, bool selected, int sceneId = 11, bool valid = true)
        {
            var path = Path.Combine(Path.GetTempPath(), $"director-auto-test-{Guid.NewGuid():N}.bin");
            if (valid)
            {
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                _paths.Add(path);
            }

            return new SceneMediaAsset
            {
                Id = id,
                FilmProjectId = 7,
                SceneId = sceneId,
                MediaType = mediaType,
                Role = role,
                FilePath = path,
                FileSize = 4,
                IsSelected = selected,
                CreatedAt = DateTime.Now
            };
        }

        public void Dispose()
        {
            foreach (var path in _paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
