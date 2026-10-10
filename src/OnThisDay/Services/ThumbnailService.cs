using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using ImageMagick;
using OnThisDay.Models;

namespace OnThisDay.Services;

public class ThumbnailService
{
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThumbnailService> _logger;
    private bool? _ffmpegAvailable;

    public ThumbnailService(
        IMemoryCache cache,
        IServiceScopeFactory scopeFactory,
        ILogger<ThumbnailService> logger)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<byte[]?> GetOrCreateThumbnailAsync(string fileHash, CancellationToken ct)
    {
        var cacheKey = $"thumb:{fileHash}";

        if (_cache.TryGetValue(cacheKey, out byte[]? cached))
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var queryService = scope.ServiceProvider.GetRequiredService<PhotoQueryService>();
        var photo = await queryService.GetPhotoByHash(fileHash);

        if (photo == null || !File.Exists(photo.FilePath))
            return null;

        byte[]? thumbBytes = photo.MediaType switch
        {
            MediaType.Photo => await GeneratePhotoThumbnail(photo.FilePath, ct),
            MediaType.Video => await GenerateVideoThumbnail(photo.FilePath, ct),
            _ => null
        };

        if (thumbBytes != null)
            CacheUntilMidnight(cacheKey, thumbBytes);

        return thumbBytes;
    }

    /// <summary>
    /// JPEG of a photo at exactly width×height, cropped around the detected persons (or centred when there
    /// are none). Without a height it fits within width instead.
    /// </summary>
    public async Task<byte[]?> GetOrCreateImageAsync(string fileHash, int width, int? height, CancellationToken ct)
    {
        var cacheKey = $"img{width}x{height}:{fileHash}";

        if (_cache.TryGetValue(cacheKey, out byte[]? cached))
            return cached;

        using var scope = _scopeFactory.CreateScope();
        var queryService = scope.ServiceProvider.GetRequiredService<PhotoQueryService>();
        var photo = await queryService.GetPhotoByHash(fileHash);

        // Photos only; a video id gets a 404
        if (photo == null || photo.MediaType != MediaType.Photo || !File.Exists(photo.FilePath))
            return null;

        // Rendered in memory and kept only in the cache, never written to disk
        byte[]? imageBytes = null;
        try
        {
            imageBytes = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var image = LoadOriented(photo.FilePath);
                return RenderImage(image, GetFocus(photo), width, height);
            }, ct);
        }
        catch (MagickException ex)
        {
            _logger.LogWarning(ex, "Failed to generate image for {File}", photo.FilePath);
        }

        if (imageBytes != null)
            CacheUntilMidnight(cacheKey, imageBytes);

        return imageBytes;
    }

    /// <summary>Decodes any supported photo (HEIC included) upright.</summary>
    public static MagickImage LoadOriented(string filePath)
    {
        var image = new MagickImage(filePath);
        try
        {
            image.AutoOrient();
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private static FocusBox? GetFocus(PhotoRecord photo) =>
        photo is { FocusX: { } x, FocusY: { } y, FocusW: { } w, FocusH: { } h }
            ? new FocusBox(x, y, w, h)
            : null;

    private static byte[] RenderImage(MagickImage image, FocusBox? focus, int width, int? height)
    {
        if (height is { } h)
        {
            var crop = FocusCrop.Compute((int)image.Width, (int)image.Height, focus, width, h);
            image.Crop(new MagickGeometry(crop.X, crop.Y, (uint)crop.W, (uint)crop.H));
            image.ResetPage();
            image.Resize(new MagickGeometry((uint)width, (uint)h) { IgnoreAspectRatio = true });
        }
        else
        {
            image.Resize(new MagickGeometry((uint)width, (uint)width)
            {
                IgnoreAspectRatio = false,
                Greater = true
            });
        }

        image.Quality = 85;
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }

    private void CacheUntilMidnight(string cacheKey, byte[] bytes)
    {
        var endOfDay = DateTime.Today.AddDays(1);
        var options = new MemoryCacheEntryOptions
        {
            Size = bytes.Length,
            AbsoluteExpiration = endOfDay
        };
        _cache.Set(cacheKey, bytes, options);
    }

    public async Task PreloadThumbnailsForDayAsync(int month, int day, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var queryService = scope.ServiceProvider.GetRequiredService<PhotoQueryService>();
        var photosByYear = await queryService.GetPhotosForDay(month, day);

        var allPhotos = photosByYear.Values.SelectMany(list => list).ToList();
        _logger.LogInformation("Preloading {Count} thumbnails for {Month}/{Day}", allPhotos.Count, month, day);

        await Parallel.ForEachAsync(allPhotos, new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        }, async (photo, token) =>
        {
            try
            {
                await GetOrCreateThumbnailAsync(photo.FileHash, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to preload thumbnail for photo {Hash}", photo.FileHash);
            }
        });

        _logger.LogInformation("Finished preloading thumbnails for {Month}/{Day}", month, day);
    }

    private async Task<byte[]?> GeneratePhotoThumbnail(string filePath, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var image = LoadOriented(filePath);
                image.Resize(new MagickGeometry(300, 300)
                {
                    IgnoreAspectRatio = false,
                    Greater = true
                });
                image.Quality = 70;
                image.Format = MagickFormat.Jpeg;
                return image.ToByteArray();
            }, ct);
        }
        catch (MagickException ex)
        {
            _logger.LogWarning(ex, "Failed to generate photo thumbnail for {File}", filePath);
            return null;
        }
    }

    private async Task<byte[]?> GenerateVideoThumbnail(string filePath, CancellationToken ct)
    {
        if (!await IsFfmpegAvailableAsync())
            return null;

        var tempPath = Path.Combine(Path.GetTempPath(), $"onthisday-thumb-{Guid.NewGuid()}.jpg");
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-ss 0.5 -i \"{filePath}\" -vframes 1 -vf \"scale='min(300,iw)':'min(300,ih)':force_original_aspect_ratio=decrease\" -q:v 5 -y \"{tempPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || !File.Exists(tempPath))
            {
                _logger.LogWarning("FFmpeg failed for {File} with exit code {Code}", filePath, process.ExitCode);
                return null;
            }

            return await File.ReadAllBytesAsync(tempPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate video thumbnail for {File}", filePath);
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private async Task<bool> IsFfmpegAvailableAsync()
    {
        if (_ffmpegAvailable.HasValue)
            return _ffmpegAvailable.Value;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            await process.WaitForExitAsync();
            _ffmpegAvailable = process.ExitCode == 0;
        }
        catch
        {
            _ffmpegAvailable = false;
        }

        if (!_ffmpegAvailable.Value)
            _logger.LogWarning("FFmpeg not found — video thumbnails will be skipped");

        return _ffmpegAvailable.Value;
    }
}
