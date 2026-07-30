namespace Director.Services.Interfaces;

public interface IImageThumbnailService
{
    Task<string?> CreateThumbnailAsync(string imagePath, CancellationToken cancellationToken = default);
}
