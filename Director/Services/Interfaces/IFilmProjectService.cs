using Director.Dtos;
using Director.Enums;
using Director.Models;

namespace Director.Services.Interfaces;

public interface IFilmProjectService
{
    Task<List<FilmProject>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<FilmProject?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<FilmProject> CreateAsync(FilmProject project, CancellationToken cancellationToken = default);

    Task UpdateAsync(FilmProject project, CancellationToken cancellationToken = default);

    Task<List<FilmProjectListItemDto>> GetProjectHistoryAsync(
        string? searchText = null,
        FilmProjectStatus? status = null,
        string? storyGenre = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(int projectId, CancellationToken cancellationToken = default);
}
