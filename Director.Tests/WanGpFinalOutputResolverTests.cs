using Director.Enums;
using Director.Models;
using Director.Options;
using Director.Services;
using Director.Services.Interfaces;
using Director.WanGp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Director.Tests;

public sealed class WanGpFinalOutputResolverTests
{
    [Theory]
    [InlineData("video_tmp.mp4")]
    [InlineData("video.TMP")]
    [InlineData("video.part")]
    [InlineData("video.partial")]
    [InlineData("video.download")]
    [InlineData("image_tmp.png")]
    [InlineData("audio_tmp.wav")]
    public void TransientSuffix_IsNeverFinal(string fileName)
    {
        Assert.True(WanGpFinalOutputResolver.IsTransientPath(fileName));
    }

    [Fact]
    public async Task TmpSnapshotThenFinalRename_ResolvesFinalPath()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Write("2026-08-03-15h48m49s_seed135033810_[Visual Direction]Single continuous cinematic sho_tmp.mp4");
        var before = scope.Resolver.CaptureSnapshot(WanGpOutputMediaKind.Video);
        File.Delete(transient);
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_[Visual Direction]Single continuous cinematic sho.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, before, [transient], seed: 135033810));

        Assert.Equal(final, result.Candidate.FilePath);
        Assert.DoesNotContain("_tmp", Path.GetFileName(result.Candidate.FilePath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TemporaryFileDeletedAndTruncatedFinalName_UsesCorrelation()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Path("2026-08-03-15h48m49s_seed135033810_prompt_tmp.mp4");
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_different truncated final.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [transient], seed: 135033810));

        Assert.Equal(final, result.Candidate.FilePath);
        Assert.Contains("SeedMatch", result.Candidate.Evidence);
    }

    [Fact]
    public async Task PromptFilenameReconstruction_IsNotRequired()
    {
        using var scope = new TempOutputScope();
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_truncated.mp4");
        var imaginaryPromptPath = scope.Path("full prompt reconstructed filename that does not exist_tmp.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [imaginaryPromptPath], seed: 135033810));

        Assert.Equal(final, result.Candidate.FilePath);
    }

    [Fact]
    public async Task OnlyTransientCandidate_TimesOutAndDoesNotResolve()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Write("2026-08-03-15h48m49s_seed135033810_bug_tmp.mp4");

        var ex = await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
            scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [transient], seed: 135033810, waitMs: 250)));

        Assert.Equal(transient, ex.TransientCandidatePath);
    }

    [Fact]
    public async Task ChangingFileSize_IsNotReady()
    {
        using var scope = new TempOutputScope();
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_growing.mp4");
        await using (File.Open(final, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
                scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [final], seed: 135033810, waitMs: 300)));
        }
    }

    [Fact]
    public async Task StableFile_PassesAfterMetadataProbe()
    {
        using var scope = new TempOutputScope();
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_stable.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [final], seed: 135033810));

        Assert.True(result.Candidate.HasVideo);
        Assert.True(result.Candidate.HasAudio);
        Assert.Equal(9.5625, result.Candidate.DurationSeconds);
    }

    [Fact]
    public async Task FfprobeFailure_IsRejected()
    {
        using var scope = new TempOutputScope();
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_bad.mp4");
        scope.Metadata.FailPaths.Add(final);

        await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
            scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [final], seed: 135033810, waitMs: 250)));
    }

    [Fact]
    public async Task MultipleCandidatesWithoutDeterministicCorrelation_AreAmbiguous()
    {
        using var scope = new TempOutputScope();
        scope.Write("a.mp4");
        scope.Write("b.mp4");

        await Assert.ThrowsAsync<WanGpAmbiguousOutputException>(() =>
            scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [], seed: null)));
    }

    [Fact]
    public async Task MultipleCandidatesWithSeedCorrelation_SelectsMatchingSeed()
    {
        using var scope = new TempOutputScope();
        scope.Write("unrelated_seed2.mp4");
        var expected = scope.Write("expected_seed135033810.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [], seed: 135033810));

        Assert.Equal(expected, result.Candidate.FilePath);
        Assert.Contains("SeedMatch", result.Candidate.Evidence);
    }

    [Fact]
    public async Task RequireAudio_RejectsVideoWithoutAudio()
    {
        using var scope = new TempOutputScope();
        var final = scope.Write("silent_seed1.mp4");
        scope.Metadata.NoAudioPaths.Add(final);
        var request = VideoRequest(scope, new WanGpOutputSnapshot(), [final], seed: 1, waitMs: 250);
        request.RequireAudio = true;

        await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
            scope.Resolver.ResolveAsync(request));
    }

    [Fact]
    public async Task TimeoutException_IncludesDiagnosticContext()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Write("diagnostic_seed42_tmp.mp4");

        var ex = await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
            scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [transient], seed: 42, waitMs: 250)));

        Assert.Equal(52, ex.JobId);
        Assert.Equal(36, ex.SceneId);
        Assert.Equal(42, ex.Seed);
        Assert.Equal(scope.OutputRoot, ex.OutputRoot);
        Assert.Equal(3, ex.LastObservedSize);
    }

    [Fact]
    public async Task TransientStemEvidence_IsRecordedForFinalizedRename()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Path("clip_seed5_tmp.mp4");
        scope.Write("clip_seed5.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [transient], seed: 5));

        Assert.Contains("TransientStemMatch", result.Candidate.Evidence);
    }

    [Fact]
    public async Task OutputRootOutsidePath_IsRejected()
    {
        using var scope = new TempOutputScope();
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(outside, [1, 2, 3]);
        try
        {
            await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
                scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [outside], seed: null, waitMs: 250)));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task PathTraversal_IsRejected()
    {
        using var scope = new TempOutputScope();
        var traversal = Path.Combine(scope.OutputRoot, "..", "escape.mp4");

        await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
            scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [traversal], seed: null, waitMs: 250)));
    }

    [Fact]
    public async Task RealBugFixture_RejectsTmpAndResolvesFinal()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Path("2026-08-03-15h48m49s_seed135033810_[Visual Direction]Single continuous cinematic sho_tmp.mp4");
        var final = scope.Write("2026-08-03-15h48m49s_seed135033810_[Visual Direction]Single continuous cinematic sho.mp4");

        var result = await scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [transient], seed: 135033810));

        Assert.Equal(final, result.Candidate.FilePath);
        Assert.False(WanGpFinalOutputResolver.IsTransientPath(result.Candidate.FilePath));
    }

    [Fact]
    public async Task AudioResolver_RejectsTransientSuffixAndResolvesFinal()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Path("voice_seed7_tmp.wav");
        var final = scope.Write("voice_seed7.wav");

        var result = await scope.Resolver.ResolveAsync(new WanGpFinalOutputResolveRequest
        {
            MediaKind = WanGpOutputMediaKind.Audio,
            BeforeSnapshot = new WanGpOutputSnapshot(),
            StartedAt = DateTime.Now.AddSeconds(-1),
            ExplicitPaths = [transient],
            Seed = 7,
            MaxWait = TimeSpan.FromSeconds(2)
        });

        Assert.Equal(final, result.Candidate.FilePath);
    }

    [Fact]
    public async Task ImageTransientSuffix_TimesOutInsteadOfResolvingTmp()
    {
        using var scope = new TempOutputScope();
        var transient = scope.Write("image_tmp.png");

        await Assert.ThrowsAsync<WanGpOutputFinalizationTimeoutException>(() =>
            scope.Resolver.ResolveAsync(new WanGpFinalOutputResolveRequest
            {
                MediaKind = WanGpOutputMediaKind.Image,
                BeforeSnapshot = new WanGpOutputSnapshot(),
                StartedAt = DateTime.Now.AddSeconds(-1),
                ExplicitPaths = [transient],
                MaxWait = TimeSpan.FromMilliseconds(250)
            }));
    }

    [Fact]
    public async Task CancellationWhileSettling_ThrowsQuickly()
    {
        using var scope = new TempOutputScope();
        scope.Write("slow_seed1.mp4");
        using var cts = new CancellationTokenSource(100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scope.Resolver.ResolveAsync(VideoRequest(scope, new WanGpOutputSnapshot(), [], seed: 1, waitMs: 5000), cts.Token));
    }

    [Fact]
    public async Task ImportGeneratedVideo_UsesDirectorStagingThenFinalName()
    {
        using var scope = new TempOutputScope();
        var source = scope.Write("source_seed1.mp4");
        var service = new MediaFileService(
            Microsoft.Extensions.Options.Options.Create(new WanGpOptions { OutputRootPath = scope.DirectorRoot, OutputDirectory = scope.OutputRoot }),
            new NoThumbnailService(),
            scope.Metadata,
            NullLogger<MediaFileService>.Instance);

        var asset = await service.CopyGeneratedVideoAsync(
            new FilmScene { Id = 36, FilmProjectId = 9, SceneNumber = 1 },
            new GenerationJob { Id = 52, ModelType = "ltx", SettingsJson = "{}" },
            source,
            scope.Metadata.ValidVideo,
            versionNumber: 2,
            isSelected: true,
            sourceImageAssetId: 31);

        Assert.True(File.Exists(asset.FilePath));
        Assert.StartsWith("scene-001-video-v002-", Path.GetFileName(asset.FilePath), StringComparison.Ordinal);
        Assert.False(asset.FilePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetFileName(source), asset.OriginalFileName);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task ImportGeneratedVideo_DestinationFingerprintMatchesSource()
    {
        using var scope = new TempOutputScope();
        var source = scope.Write("source_seed1.mp4");
        var service = new MediaFileService(
            Microsoft.Extensions.Options.Options.Create(new WanGpOptions { OutputRootPath = scope.DirectorRoot, OutputDirectory = scope.OutputRoot }),
            new NoThumbnailService(),
            scope.Metadata,
            NullLogger<MediaFileService>.Instance);

        var asset = await service.CopyGeneratedVideoAsync(
            new FilmScene { Id = 36, FilmProjectId = 9, SceneNumber = 1 },
            new GenerationJob { Id = 52, ModelType = "ltx", SettingsJson = "{}" },
            source,
            scope.Metadata.ValidVideo,
            versionNumber: 1,
            isSelected: true,
            sourceImageAssetId: 31);

        Assert.Equal(await Sha256Async(source), await Sha256Async(asset.FilePath));
    }

    [Fact]
    public async Task ImportGeneratedVideo_DoesNotUsePromptDerivedFileName()
    {
        using var scope = new TempOutputScope();
        var source = scope.Write("2026-08-03-15h48m49s_seed135033810_[Visual Direction]Single continuous cinematic sho.mp4");
        var service = new MediaFileService(
            Microsoft.Extensions.Options.Options.Create(new WanGpOptions { OutputRootPath = scope.DirectorRoot, OutputDirectory = scope.OutputRoot }),
            new NoThumbnailService(),
            scope.Metadata,
            NullLogger<MediaFileService>.Instance);

        var asset = await service.CopyGeneratedVideoAsync(
            new FilmScene { Id = 36, FilmProjectId = 9, SceneNumber = 1 },
            new GenerationJob { Id = 52, ModelType = "ltx", SettingsJson = "{}" },
            source,
            scope.Metadata.ValidVideo,
            versionNumber: 3,
            isSelected: true,
            sourceImageAssetId: 31);

        Assert.StartsWith("scene-001-video-v003-", Path.GetFileName(asset.FilePath), StringComparison.Ordinal);
        Assert.DoesNotContain("Visual Direction", Path.GetFileName(asset.FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportGeneratedVideo_CopyFailureKeepsSourceAndCleansStaging()
    {
        using var scope = new TempOutputScope();
        var source = scope.Write("source_seed1.mp4");
        scope.Metadata.FailEveryProbe = true;
        var service = new MediaFileService(
            Microsoft.Extensions.Options.Options.Create(new WanGpOptions { OutputRootPath = scope.DirectorRoot, OutputDirectory = scope.OutputRoot }),
            new NoThumbnailService(),
            scope.Metadata,
            NullLogger<MediaFileService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CopyGeneratedVideoAsync(
            new FilmScene { Id = 36, FilmProjectId = 9, SceneNumber = 1 },
            new GenerationJob { Id = 52, ModelType = "ltx", SettingsJson = "{}" },
            source,
            scope.Metadata.ValidVideo,
            versionNumber: 2,
            isSelected: true,
            sourceImageAssetId: 31));

        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(scope.DirectorRoot, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ImportedPreviewPath_IsDirectorOwnedPath()
    {
        using var scope = new TempOutputScope();
        var source = scope.Write("source_seed1.mp4");
        var service = new MediaFileService(
            Microsoft.Extensions.Options.Options.Create(new WanGpOptions { OutputRootPath = scope.DirectorRoot, OutputDirectory = scope.OutputRoot }),
            new NoThumbnailService(),
            scope.Metadata,
            NullLogger<MediaFileService>.Instance);

        var asset = await service.CopyGeneratedVideoAsync(
            new FilmScene { Id = 36, FilmProjectId = 9, SceneNumber = 1 },
            new GenerationJob { Id = 52, ModelType = "ltx", SettingsJson = "{}" },
            source,
            scope.Metadata.ValidVideo,
            versionNumber: 1,
            isSelected: true,
            sourceImageAssetId: 31);

        Assert.StartsWith(scope.DirectorRoot, asset.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(asset.FilePath.StartsWith(scope.OutputRoot, StringComparison.OrdinalIgnoreCase));
    }

    private static WanGpFinalOutputResolveRequest VideoRequest(
        TempOutputScope scope,
        WanGpOutputSnapshot before,
        IReadOnlyList<string> explicitPaths,
        int? seed,
        int waitMs = 2000) =>
        new WanGpFinalOutputResolveRequest
        {
            MediaKind = WanGpOutputMediaKind.Video,
            BeforeSnapshot = before,
            StartedAt = DateTime.Now.AddSeconds(-2),
            CompletedAt = DateTime.Now,
            ExplicitPaths = explicitPaths,
            ExternalJobId = "job-1",
            JobId = 52,
            SceneId = 36,
            Seed = seed,
            MaxWait = TimeSpan.FromMilliseconds(waitMs)
        };

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private sealed class TempOutputScope : IDisposable
    {
        public TempOutputScope()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DirectorWanGpResolverTests", Guid.NewGuid().ToString("N"));
            OutputRoot = System.IO.Path.Combine(Root, "outputs");
            DirectorRoot = System.IO.Path.Combine(Root, "director");
            Directory.CreateDirectory(OutputRoot);
            Directory.CreateDirectory(DirectorRoot);
            Metadata = new FakeVideoMetadataService();
            Resolver = new WanGpFinalOutputResolver(
                Microsoft.Extensions.Options.Options.Create(new WanGpOptions { RootPath = Root, OutputDirectory = OutputRoot }),
                Metadata);
        }

        public string Root { get; }
        public string OutputRoot { get; }
        public string DirectorRoot { get; }
        public FakeVideoMetadataService Metadata { get; }
        public WanGpFinalOutputResolver Resolver { get; }

        public string Path(string name) => System.IO.Path.GetFullPath(System.IO.Path.Combine(OutputRoot, name));

        public string Write(string name)
        {
            var path = Path(name);
            File.WriteAllBytes(path, [1, 2, 3]);
            File.SetLastWriteTime(path, DateTime.Now);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class FakeVideoMetadataService : IVideoMetadataService
    {
        public HashSet<string> FailPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> NoAudioPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailEveryProbe { get; set; }
        public VideoMetadata ValidVideo { get; } = new()
        {
            HasVideo = true,
            HasAudio = true,
            Width = 1344,
            Height = 768,
            DurationSeconds = 9.5625,
            AudioDurationSeconds = 9.557,
            Fps = 16,
            Codec = "h264",
            AudioCodec = "aac"
        };

        public Task<VideoMetadata> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
        {
            if (FailEveryProbe || FailPaths.Contains(videoPath))
            {
                return Task.FromResult(new VideoMetadata());
            }

            if (NoAudioPaths.Contains(videoPath))
            {
                return Task.FromResult(new VideoMetadata
                {
                    HasVideo = true,
                    HasAudio = false,
                    Width = 1344,
                    Height = 768,
                    DurationSeconds = 9.5625,
                    Fps = 16
                });
            }

            return Task.FromResult(ValidVideo);
        }
    }

    private sealed class NoThumbnailService : IImageThumbnailService
    {
        public Task<string?> CreateThumbnailAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
