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
            entity.Property(asset => asset.FilePath).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(asset => asset.ThumbnailPath).HasColumnType("nvarchar(max)");
            entity.Property(asset => asset.OriginalFileName).IsRequired().HasMaxLength(260);
            entity.Property(asset => asset.FileExtension).IsRequired().HasMaxLength(20);
            entity.Property(asset => asset.ModelType).IsRequired().HasMaxLength(160);
            entity.Property(asset => asset.MetadataJson).IsRequired().HasColumnType("nvarchar(max)");
            entity.Property(asset => asset.CreatedAt).HasDefaultValueSql("GETDATE()").IsRequired();
        });
    }
}
