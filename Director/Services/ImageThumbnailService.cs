using System.IO;
using System.Windows.Media.Imaging;
using Director.Services.Interfaces;

namespace Director.Services;

public sealed class ImageThumbnailService : IImageThumbnailService
{
    public async Task<string?> CreateThumbnailAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(imagePath);
            if (directory is null)
            {
                return null;
            }

            var thumbnailPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + "_thumb.jpg");
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath);
            bitmap.DecodePixelWidth = 320;
            bitmap.EndInit();
            bitmap.Freeze();

            var encoder = new JpegBitmapEncoder { QualityLevel = 82 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(thumbnailPath);
            encoder.Save(stream);
            return thumbnailPath;
        }, cancellationToken);
    }
}
