using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using OnThisDay.Models;
using System.Globalization;

namespace OnThisDay.Services;

public class ExifService
{
    private static readonly string[] ExifDateFormats =
    [
        "yyyy:MM:dd HH:mm:ss",
        "yyyy:MM:dd HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss",
    ];

    public static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tiff", ".tif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm"
    };

    private readonly ILogger<ExifService> _logger;

    public ExifService(ILogger<ExifService> logger)
    {
        _logger = logger;
    }

    public static MediaType GetMediaType(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return VideoExtensions.Contains(ext) ? MediaType.Video : MediaType.Photo;
    }

    public (DateTime dateTaken, DateSource source) ExtractDate(string filePath)
    {
        // Videos don't have EXIF data — go straight to file system date
        if (GetMediaType(filePath) == MediaType.Video)
        {
            var videoLastWrite = File.GetLastWriteTime(filePath);
            _logger.LogDebug("Using file LastWriteTime for video {File}: {Date}", filePath, videoLastWrite);
            return (videoLastWrite, DateSource.FileSystem);
        }

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(filePath);

            // Try DateTimeOriginal first (gold standard)
            var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
            if (subIfd != null)
            {
                var dateOriginal = subIfd.GetDescription(ExifDirectoryBase.TagDateTimeOriginal);
                if (TryParseExifDate(dateOriginal, out var dt))
                {
                    _logger.LogDebug("EXIF DateTimeOriginal for {File}: {Date}", filePath, dt);
                    return (dt, DateSource.ExifDateTimeOriginal);
                }
            }

            // Fallback to EXIF DateTime
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            if (ifd0 != null)
            {
                var dateTime = ifd0.GetDescription(ExifDirectoryBase.TagDateTime);
                if (TryParseExifDate(dateTime, out var dt))
                {
                    _logger.LogDebug("EXIF DateTime for {File}: {Date}", filePath, dt);
                    return (dt, DateSource.ExifDateTime);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read EXIF from {File}", filePath);
        }

        // Final fallback: file system LastWriteTime
        var lastWrite = File.GetLastWriteTime(filePath);
        _logger.LogDebug("Using file LastWriteTime for {File}: {Date}", filePath, lastWrite);
        return (lastWrite, DateSource.FileSystem);
    }

    private static bool TryParseExifDate(string? dateString, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(dateString))
            return false;

        return DateTime.TryParseExact(
            dateString.Trim(),
            ExifDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out result);
    }
}
