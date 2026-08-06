using Director.Data;
using Director.Dtos;
using Director.Enums;
using Director.Models;
using Director.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Director.Services;

public class FilmProjectService : IFilmProjectService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public FilmProjectService(IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<List<FilmProject>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.FilmProjects
            .AsNoTracking()
            .OrderByDescending(project => project.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<FilmProject?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.FilmProjects
            .AsNoTracking()
            .FirstOrDefaultAsync(project => project.Id == id, cancellationToken);
    }

    public async Task<FilmProject> CreateAsync(FilmProject project, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        project.CreatedAt = DateTime.Now;
        dbContext.FilmProjects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task UpdateAsync(FilmProject project, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        project.UpdatedAt = DateTime.Now;
        dbContext.FilmProjects.Update(project);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FilmProjectListItemDto>> GetProjectHistoryAsync(
        string? searchText = null,
        FilmProjectStatus? status = null,
        string? storyGenre = null,
        CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.FilmProjects.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(project => project.ProjectName.Contains(searchText) || project.Subject.Contains(searchText));
        }

        if (status is not null)
        {
            query = query.Where(project => project.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(storyGenre))
        {
            query = query.Where(project => project.StoryGenre == storyGenre);
        }

        return await query
            .OrderByDescending(project => project.CreatedAt)
            .Select(project => new FilmProjectListItemDto
            {
                Id = project.Id,
                ProjectName = project.ProjectName,
                SubjectPreview = project.Subject.Length > 180 ? project.Subject.Substring(0, 180) + "..." : project.Subject,
                TotalDurationMinutes = project.TotalDurationMinutes,
                ClipDurationSeconds = project.ClipDurationSeconds,
                CalculatedClipCount = project.CalculatedClipCount,
                StoryGenre = project.StoryGenre,
                VisualStyle = project.VisualStyle,
                Resolution = project.Resolution,
                Status = project.Status,
                CreatedAt = project.CreatedAt,
                UpdatedAt = project.UpdatedAt,
                HasStory = project.Story != null,
                GeneratedSceneCount = project.Scenes.Count,
                HasAutonomousGenerationRun = project.AutonomousGenerationRuns.Any(),
                AutonomousGenerationStatus = project.AutonomousGenerationRuns
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (AutonomousGenerationRunStatus?)run.Status)
                    .FirstOrDefault(),
                AutonomousGenerationStage = project.AutonomousGenerationRuns
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => (AutonomousGenerationStage?)run.CurrentStage)
                    .FirstOrDefault(),
                AutonomousGenerationProgressPercentage = project.AutonomousGenerationRuns
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.OverallProgressPercentage)
                    .FirstOrDefault(),
                AutonomousGenerationLastHeartbeatAtUtc = project.AutonomousGenerationRuns
                    .OrderByDescending(run => run.StartedAtUtc)
                    .Select(run => run.LastHeartbeatAtUtc)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
    }

    public async Task DeleteAsync(int projectId, CancellationToken cancellationToken = default)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await dbContext.FilmProjects.FirstOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        if (project is null)
        {
            return;
        }

        dbContext.FilmProjects.Remove(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
