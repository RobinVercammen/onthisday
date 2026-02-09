using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using OnThisDay.Data;
using OnThisDay.Models;

namespace OnThisDay.Services;

public class PhotoIndexingService : BackgroundService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tiff", ".tif",
        ".mp4", ".mov", ".avi", ".mkv", ".webm"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PhotoIndexingService> _logger;
    private readonly List<string> _photoDirectories;
    private readonly TimeSpan _rescanInterval;

    public PhotoIndexingService(
        IServiceScopeFactory scopeFactory,
        ILogger<PhotoIndexingService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var dirs = configuration["PHOTO_DIRECTORIES"] ?? "";
        _photoDirectories = dirs
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var hours = double.TryParse(configuration["RESCAN_INTERVAL_HOURS"], out var h) ? h : 6;
        _rescanInterval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let the app start up
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAllDirectories(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during photo indexing scan");
            }

            _logger.LogInformation("Next rescan in {Interval}", _rescanInterval);
            await Task.Delay(_rescanInterval, stoppingToken);
        }
    }

    private async Task ScanAllDirectories(CancellationToken ct)
    {
        if (_photoDirectories.Count == 0)
        {
            _logger.LogWarning("No photo directories configured. Set PHOTO_DIRECTORIES env var.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var exifService = scope.ServiceProvider.GetRequiredService<ExifService>();

        // Load all existing records into a dictionary for O(1) lookup
        var existingRecords = await db.Photos
            .ToDictionaryAsync(p => p.FilePath, p => p, StringComparer.OrdinalIgnoreCase, ct);

        // Cache directory listings for sibling detection: dir -> set of filenames
        var dirCache = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> GetDirFiles(string dir)
        {
            return dirCache.GetOrAdd(dir, d =>
                new HashSet<string>(
                    Directory.EnumerateFiles(d).Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase));
        }

        bool HasPhotoSibling(string movPath)
        {
            var dir = Path.GetDirectoryName(movPath)!;
            var stem = Path.GetFileNameWithoutExtension(movPath);
            var files = GetDirFiles(dir);
            return ExifService.PhotoExtensions.Any(pe => files.Contains(stem + pe));
        }

        string? FindMovSibling(string photoPath)
        {
            var dir = Path.GetDirectoryName(photoPath)!;
            var stem = Path.GetFileNameWithoutExtension(photoPath);
            var files = GetDirFiles(dir);
            var movName = files.FirstOrDefault(f => f.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(f).Equals(".mov", StringComparison.OrdinalIgnoreCase));
            return movName != null ? Path.Combine(dir, movName) : null;
        }

        // Collect all files to index, filtering out Live Photo companions
        var filesToIndex = new List<string>();
        foreach (var dir in _photoDirectories)
        {
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Photo directory not found: {Dir}", dir);
                continue;
            }

            _logger.LogInformation("Scanning directory: {Dir}", dir);

            foreach (var filePath in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var ext = Path.GetExtension(filePath);
                if (!SupportedExtensions.Contains(ext))
                    continue;

                // Skip Live Photo companion .mov files
                if (ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) && HasPhotoSibling(filePath))
                    continue;

                filesToIndex.Add(filePath);
            }
        }

        var allFilesOnDisk = new HashSet<string>(filesToIndex, StringComparer.OrdinalIgnoreCase);
        _logger.LogInformation("Found {Count} files to index", filesToIndex.Count);

        // Extract EXIF data in parallel using a producer-consumer pattern
        var channel = Channel.CreateUnbounded<(string filePath, FileInfo fileInfo, DateTime dateTaken, DateSource source, MediaType mediaType, string? movSibling)>(
            new UnboundedChannelOptions { SingleReader = true });

        var producerTask = Task.Run(() =>
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            };

            Parallel.ForEach(filesToIndex, parallelOptions, filePath =>
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);

                    // Skip unchanged files early
                    if (existingRecords.TryGetValue(filePath, out var existing)
                        && existing.FileSize == fileInfo.Length
                        && existing.FileLastModified == fileInfo.LastWriteTimeUtc)
                    {
                        return;
                    }

                    var (dateTaken, source) = exifService.ExtractDate(filePath);
                    var mediaType = ExifService.GetMediaType(filePath);
                    var movSibling = mediaType == MediaType.Photo ? FindMovSibling(filePath) : null;

                    channel.Writer.TryWrite((filePath, fileInfo, dateTaken, source, mediaType, movSibling));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read file: {File}", filePath);
                }
            });

            channel.Writer.Complete();
        }, ct);

        // Consume results and update DB
        var newCount = 0;
        var updatedCount = 0;

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            if (existingRecords.TryGetValue(item.filePath, out var existing))
            {
                existing.DateTaken = item.dateTaken;
                existing.Month = item.dateTaken.Month;
                existing.Day = item.dateTaken.Day;
                existing.Year = item.dateTaken.Year;
                existing.DateSource = item.source;
                existing.FileSize = item.fileInfo.Length;
                existing.FileLastModified = item.fileInfo.LastWriteTimeUtc;
                existing.IndexedAt = DateTime.UtcNow;
                existing.MediaType = item.mediaType;
                existing.LivePhotoMovPath = item.movSibling;
                updatedCount++;
            }
            else
            {
                db.Photos.Add(new PhotoRecord
                {
                    FilePath = item.filePath,
                    FileName = item.fileInfo.Name,
                    DateTaken = item.dateTaken,
                    Month = item.dateTaken.Month,
                    Day = item.dateTaken.Day,
                    Year = item.dateTaken.Year,
                    DateSource = item.source,
                    FileSize = item.fileInfo.Length,
                    FileLastModified = item.fileInfo.LastWriteTimeUtc,
                    IndexedAt = DateTime.UtcNow,
                    MediaType = item.mediaType,
                    LivePhotoMovPath = item.movSibling
                });
                newCount++;
            }

            // Batch save every 500 records
            if ((newCount + updatedCount) % 500 == 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Progress: {New} new, {Updated} updated so far...", newCount, updatedCount);
            }
        }

        await producerTask;
        await db.SaveChangesAsync(ct);

        // Prune: bulk remove DB entries for files no longer on disk
        var toRemove = existingRecords.Keys
            .Where(path => !allFilesOnDisk.Contains(path))
            .ToList();

        if (toRemove.Count > 0)
        {
            await db.Photos
                .Where(p => toRemove.Contains(p.FilePath))
                .ExecuteDeleteAsync(ct);
        }

        _logger.LogInformation(
            "Indexing complete: {New} new, {Updated} updated, {Pruned} pruned. Total: {Total}",
            newCount, updatedCount, toRemove.Count, await db.Photos.CountAsync(ct));
    }
}
