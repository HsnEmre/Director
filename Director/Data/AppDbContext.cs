using Director.Enums;
using Director.Models;
using Microsoft.EntityFrameworkCore;

namespace Director.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<FilmProject> FilmProjects => Set<FilmProject>();
    public DbSet<FilmStory> FilmStories => Set<FilmStory>();
    public DbSet<StoryCharacter> StoryCharacters => Set<StoryCharacter>();
    public DbSet<FilmScene> FilmScenes => Set<FilmScene>();
    public DbSet<GenerationJob> GenerationJobs => Set<GenerationJob>();
    public DbSet<SceneMediaAsset> SceneMediaAssets => Set<SceneMediaAsset>();
    public DbSet<CharacterVoiceProfile> CharacterVoiceProfiles => Set<CharacterVoiceProfile>();
    public DbSet<SceneSpeechPlan> SceneSpeechPlans => Set<SceneSpeechPlan>();
    public DbSet<SceneSpeechSegment> SceneSpeechSegments => Set<SceneSpeechSegment>();
    public DbSet<LtxNativeVoiceProfile> LtxNativeVoiceProfiles => Set<LtxNativeVoiceProfile>();
    public DbSet<AutonomousGenerationRun> AutonomousGenerationRuns => Set<AutonomousGenerationRun>();
    public DbSet<AutonomousSceneWorkItem> AutonomousSceneWorkItems => Set<AutonomousSceneWorkItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FilmProject>(entity =>
        {
            entity.HasKey(project => project.Id);

            entity.Property(project => project.ProjectName)
                .IsRequired()
                .HasMaxLength(200);

            entity.Property(project => project.Subject)
                .IsRequired()
                .HasColumnType("nvarchar(max)");

            entity.Property(project => project.TotalDurationMinutes)
                .IsRequired();

            entity.Property(project => project.ClipDurationSeconds)
                .IsRequired();

            entity.Property(project => project.CalculatedClipCount)
                .IsRequired();

            entity.Property(project => project.Language)
                .IsRequired()
                .HasMaxLength(50);

            entity.Property(project => project.TargetAudience)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(project => project.StoryGenre)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(project => project.VisualStyle)
                .IsRequired()
                .HasMaxLength(150);

            entity.Property(project => project.VideoStyle)
                .IsRequired()
                .HasMaxLength(150);

            entity.Property(project => project.AspectRatio)
                .IsRequired()
                .HasMaxLength(30);

            entity.Property(project => project.Resolution)
                .IsRequired()
                .HasMaxLength(30);

            entity.Property(project => project.NarratorTone)
                .HasMaxLength(150);

            entity.Property(project => project.MainCharacterDescription)
                .HasColumnType("nvarchar(max)");

            entity.Property(project => project.AdditionalInstructions)
                .HasColumnType("nvarchar(max)");

            entity.Property(project => project.Status)
                .HasConversion<int>()
                .HasDefaultValue(FilmProjectStatus.Draft)
                .IsRequired();

            entity.Property(project => project.CreatedAt)
                .HasDefaultValueSql("GETDATE()")
                .IsRequired();
        });

        modelBuilder.Entity<FilmStory>(entity =>
        {
            entity.HasKey(story => story.Id);

            entity.HasIndex(story => story.FilmProjectId)
                .IsUnique();

            entity.HasOne(story => story.FilmProject)
                .WithOne(project => project.Story)
                .HasForeignKey<FilmStory>(story => story.FilmProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(story => story.Title).IsRequired().HasMaxLength(250);
            entity.Property(story => story.Logline).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.Synopsis).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.OpeningSummary).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.DevelopmentSummary).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.ClimaxSummary).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.EndingSummary).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.WorldDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.VisualDirection).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.ContinuityRulesJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(story => story.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<StoryCharacter>(entity =>
        {
            entity.HasKey(character => character.Id);

            entity.HasIndex(character => new { character.FilmStoryId, character.CharacterKey })
                .IsUnique();

            entity.HasOne(character => character.FilmStory)
                .WithMany(story => story.Characters)
                .HasForeignKey(character => character.FilmStoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(character => character.CharacterKey).IsRequired().HasMaxLength(80);
            entity.Property(character => character.Name).IsRequired().HasMaxLength(160);
            entity.Property(character => character.Role).IsRequired().HasMaxLength(120);
            entity.Property(character => character.PhysicalDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(character => character.ClothingDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(character => character.PersonalityDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(character => character.VoiceDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(character => character.ContinuityDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(character => character.ForbiddenChangesJson).IsRequired().HasColumnType("nvarchar(max)");
        });

        modelBuilder.Entity<FilmScene>(entity =>
        {
            entity.HasKey(scene => scene.Id);

            entity.HasIndex(scene => new { scene.FilmProjectId, scene.SceneNumber })
                .IsUnique();

            entity.HasOne(scene => scene.FilmProject)
                .WithMany(project => project.Scenes)
                .HasForeignKey(scene => scene.FilmProjectId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(scene => scene.FilmStory)
                .WithMany(story => story.Scenes)
                .HasForeignKey(scene => scene.FilmStoryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(scene => scene.Title).IsRequired().HasMaxLength(250);
            entity.Property(scene => scene.StoryBeat).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.SceneDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.LocationDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.TimeOfDay).IsRequired().HasMaxLength(120);
            entity.Property(scene => scene.CharactersJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.ContinuityFromPreviousScene).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.ImagePrompt).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.ImageNegativePrompt).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.VideoPrompt).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.VideoNegativePrompt).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.NarrationText).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.DialogueJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.ValidationChecklistJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(scene => scene.Status).HasConversion<int>().IsRequired();
            entity.Property(scene => scene.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<GenerationJob>(entity =>
        {
            entity.HasKey(job => job.Id);
            entity.HasIndex(job => job.ExternalJobId);
            entity.HasIndex(job => new { job.SceneId, job.MediaType, job.Status });

            entity.HasOne(job => job.FilmProject)
                .WithMany(project => project.GenerationJobs)
                .HasForeignKey(job => job.FilmProjectId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(job => job.Scene)
                .WithMany(scene => scene.GenerationJobs)
                .HasForeignKey(job => job.SceneId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(job => job.SourceMediaAsset)
                .WithMany()
                .HasForeignKey(job => job.SourceMediaAssetId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(job => job.MediaType).HasConversion<int>().IsRequired();
            entity.Property(job => job.Provider).HasConversion<int>().IsRequired();
            entity.Property(job => job.Status).HasConversion<int>().IsRequired();
            entity.Property(job => job.ExternalJobId).HasMaxLength(200);
            entity.Property(job => job.ModelType).IsRequired().HasMaxLength(160);
            entity.Property(job => job.Prompt).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(job => job.NegativePrompt).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(job => job.SettingsJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(job => job.CurrentPhase).IsRequired().HasMaxLength(160);
            entity.Property(job => job.ErrorMessage).HasColumnType("nvarchar(max)");
            entity.Property(job => job.PromptPreparationModel).HasMaxLength(160);
            entity.Property(job => job.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<SceneMediaAsset>(entity =>
        {
            entity.HasKey(asset => asset.Id);
            entity.HasIndex(asset => new { asset.SceneId, asset.MediaType, asset.IsSelected });
            entity.HasIndex(asset => asset.GenerationJobId);

            entity.HasOne(asset => asset.FilmProject)
                .WithMany(project => project.MediaAssets)
                .HasForeignKey(asset => asset.FilmProjectId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(asset => asset.Scene)
                .WithMany(scene => scene.MediaAssets)
                .HasForeignKey(asset => asset.SceneId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(asset => asset.GenerationJob)
                .WithMany(job => job.Assets)
                .HasForeignKey(asset => asset.GenerationJobId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(asset => asset.SourceMediaAsset)
                .WithMany()
                .HasForeignKey(asset => asset.SourceMediaAssetId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(asset => asset.MediaType).HasConversion<int>().IsRequired();
            entity.Property(asset => asset.Role).HasConversion<int>().HasDefaultValue(MediaAssetRole.ReferenceImage).IsRequired();
            entity.Property(asset => asset.FilePath).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(asset => asset.ThumbnailPath).HasColumnType("nvarchar(max)");
            entity.Property(asset => asset.OriginalFileName).IsRequired().HasMaxLength(260);
            entity.Property(asset => asset.FileExtension).IsRequired().HasMaxLength(20);
            entity.Property(asset => asset.ModelType).IsRequired().HasMaxLength(160);
            entity.Property(asset => asset.MetadataJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(asset => asset.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<CharacterVoiceProfile>(entity =>
        {
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => new { profile.FilmProjectId, profile.StoryCharacterId, profile.IsDefault });
            entity.HasIndex(profile => new { profile.FilmProjectId, profile.IsNarrator, profile.IsDefault });
            entity.HasIndex(profile => new { profile.FilmProjectId, profile.StoryCharacterId, profile.IsDefault, profile.IsLocked })
                .IsUnique()
                .HasFilter("[StoryCharacterId] IS NOT NULL AND [IsDefault] = 1 AND [IsLocked] = 1");

            entity.HasOne(profile => profile.FilmProject)
                .WithMany()
                .HasForeignKey(profile => profile.FilmProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(profile => profile.StoryCharacter)
                .WithMany()
                .HasForeignKey(profile => profile.StoryCharacterId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(profile => profile.Provider).HasConversion<int>().IsRequired();
            entity.Property(profile => profile.ProfileName).IsRequired().HasMaxLength(160);
            entity.Property(profile => profile.ModelType).IsRequired().HasMaxLength(160);
            entity.Property(profile => profile.VoicePresetKey).IsRequired().HasMaxLength(160);
            entity.Property(profile => profile.VoicePresetDisplayName).IsRequired().HasMaxLength(200);
            entity.Property(profile => profile.Language).IsRequired().HasMaxLength(30);
            entity.Property(profile => profile.EmotionStyle).IsRequired().HasMaxLength(120);
            entity.Property(profile => profile.SettingsHash).IsRequired().HasMaxLength(96);
            entity.Property(profile => profile.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<SceneSpeechPlan>(entity =>
        {
            entity.HasKey(plan => plan.Id);
            entity.HasIndex(plan => plan.SceneId).IsUnique();

            entity.HasOne(plan => plan.FilmProject)
                .WithMany()
                .HasForeignKey(plan => plan.FilmProjectId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(plan => plan.Scene)
                .WithOne()
                .HasForeignKey<SceneSpeechPlan>(plan => plan.SceneId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(plan => plan.Status).HasConversion<int>().IsRequired();
            entity.Property(plan => plan.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<SceneSpeechSegment>(entity =>
        {
            entity.HasKey(segment => segment.Id);
            entity.HasIndex(segment => new { segment.SceneSpeechPlanId, segment.SortOrder });

            entity.HasOne(segment => segment.SceneSpeechPlan)
                .WithMany(plan => plan.Segments)
                .HasForeignKey(segment => segment.SceneSpeechPlanId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(segment => segment.StoryCharacter)
                .WithMany()
                .HasForeignKey(segment => segment.StoryCharacterId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(segment => segment.VoiceProfile)
                .WithMany()
                .HasForeignKey(segment => segment.VoiceProfileId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(segment => segment.SpeakerType).HasConversion<int>().IsRequired();
            entity.Property(segment => segment.SpeakerKey).IsRequired().HasMaxLength(120);
            entity.Property(segment => segment.SourceText).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(segment => segment.TurkishText).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(segment => segment.Emotion).IsRequired().HasMaxLength(120);
            entity.Property(segment => segment.Status).HasConversion<int>().IsRequired();
            entity.Property(segment => segment.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<LtxNativeVoiceProfile>(entity =>
        {
            entity.HasKey(profile => profile.Id);
            entity.HasIndex(profile => new { profile.FilmProjectId, profile.StoryCharacterId }).IsUnique();

            entity.HasOne(profile => profile.FilmProject)
                .WithMany()
                .HasForeignKey(profile => profile.FilmProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(profile => profile.StoryCharacter)
                .WithMany()
                .HasForeignKey(profile => profile.StoryCharacterId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(profile => profile.VoiceDescription).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(profile => profile.Language).IsRequired().HasMaxLength(30);
            entity.Property(profile => profile.SpeakingStyle).IsRequired().HasMaxLength(160);
            entity.Property(profile => profile.PerceivedAge).IsRequired().HasMaxLength(80);
            entity.Property(profile => profile.GenderPresentation).IsRequired().HasMaxLength(80);
            entity.Property(profile => profile.AccentDescription).IsRequired().HasMaxLength(180);
            entity.Property(profile => profile.PitchDescription).IsRequired().HasMaxLength(120);
            entity.Property(profile => profile.TempoDescription).IsRequired().HasMaxLength(120);
            entity.Property(profile => profile.SettingsHash).IsRequired().HasMaxLength(96);
            entity.Property(profile => profile.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });

        modelBuilder.Entity<AutonomousGenerationRun>(entity =>
        {
            entity.HasKey(run => run.Id);
            entity.HasIndex(run => run.CorrelationId);
            entity.HasIndex(run => run.FilmProjectId)
                .IsUnique()
                .HasDatabaseName("IX_AutonomousGenerationRuns_FilmProjectId_Active")
                .HasFilter("[Status] IN (0, 1, 2, 3, 4, 5, 6, 7, 10, 12, 13, 14, 15, 16, 17)");

            entity.HasOne(run => run.FilmProject)
                .WithMany(project => project.AutonomousGenerationRuns)
                .HasForeignKey(run => run.FilmProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(run => run.CurrentScene)
                .WithMany()
                .HasForeignKey(run => run.CurrentSceneId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(run => run.Status).HasConversion<int>().IsRequired();
            entity.Property(run => run.CurrentStage).HasConversion<int>().IsRequired();
            entity.Property(run => run.ConfigurationSnapshotJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(run => run.CorrelationId).IsRequired().HasMaxLength(64);
            entity.Property(run => run.WorkerId).HasMaxLength(64);
            entity.Property(run => run.LastError).HasColumnType("nvarchar(max)");
            entity.Property(run => run.LastMessage).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(run => run.StartedAtUtc).IsRequired();
            entity.Property(run => run.UpdatedAtUtc).IsRequired();
            entity.Property(run => run.LastHeartbeatAtUtc).IsRequired();
        });

        modelBuilder.Entity<AutonomousSceneWorkItem>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.AutonomousGenerationRunId, item.SceneNumber }).IsUnique();
            entity.HasIndex(item => new { item.StorySceneId, item.AutonomousGenerationRunId }).IsUnique();
            entity.HasIndex(item => new { item.StorySceneId, item.ImageStatus });
            entity.HasIndex(item => new { item.StorySceneId, item.VideoStatus });
            entity.HasIndex(item => new { item.StorySceneId, item.AudioStatus });

            entity.HasOne(item => item.AutonomousGenerationRun)
                .WithMany(run => run.WorkItems)
                .HasForeignKey(item => item.AutonomousGenerationRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(item => item.StoryScene)
                .WithMany()
                .HasForeignKey(item => item.StorySceneId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(item => item.ImageMediaAsset)
                .WithMany()
                .HasForeignKey(item => item.ImageMediaAssetId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(item => item.VideoMediaAsset)
                .WithMany()
                .HasForeignKey(item => item.VideoMediaAssetId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(item => item.AudioMediaAsset)
                .WithMany()
                .HasForeignKey(item => item.AudioMediaAssetId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.Property(item => item.ImageStatus).HasConversion<int>().IsRequired();
            entity.Property(item => item.VideoStatus).HasConversion<int>().IsRequired();
            entity.Property(item => item.AudioStatus).HasConversion<int>().IsRequired();
            entity.Property(item => item.FinalizationStatus).HasConversion<int>().IsRequired();
            entity.Property(item => item.LastError).HasColumnType("nvarchar(max)");
            entity.Property(item => item.UpdatedAtUtc).IsRequired();
        });
    }
}
