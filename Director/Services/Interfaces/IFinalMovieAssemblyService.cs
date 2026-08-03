using Director.Models;

namespace Director.Services.Interfaces;

public interface IFinalMovieAssemblyService
{
    Task<string> AssembleLtxNativeDialogueMovieAsync(int filmProjectId, CancellationToken cancellationToken = default);
}
