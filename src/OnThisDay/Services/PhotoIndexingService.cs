using Microsoft.EntityFrameworkCore;
using OnThisDay.Data;
using OnThisDay.Models;

namespace OnThisDay.Services;

public class PhotoIndexingService : BackgroundService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".tiff", ".tif"
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

        var allFilesOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newCount = 0;
        var updatedCount = 0;

        foreach (var dir in _photoDirectories)
        {
            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("Photo directory not found: {Dir}", dir);
                continue;
            }

            _logger.LogInformation("Scanning directory: {Dir}", dir);

            var files = Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)));

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();
                allFilesOnDisk.Add(filePath);

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var existing = await db.Photos
                        .FirstOrDefaultAsync(p => p.FilePath == filePath, ct);

                    if (existing != null)
                    {
                        // Check if file changed (size or modified date)
                        if (existing.FileSize == fileInfo.Length &&
                            existing.FileLastModified == fileInfo.LastWriteTimeUtc)
                        {
                            continue; // No changes
                        }

                        // Re-index changed file
                        var (dateTaken, source) = exifService.ExtractDate(filePath);
                        existing.DateTaken = dateTaken;
                        existing.Month = dateTaken.Month;
                        existing.Day = dateTaken.Day;
                        existing.Year = dateTaken.Year;
                        existing.DateSource = source;
                        existing.FileSize = fileInfo.Length;
                        existing.FileLastModified = fileInfo.LastWriteTimeUtc;
                        existing.IndexedAt = DateTime.UtcNow;
                        updatedCount++;
                    }
                    else
                    {
                        // New file
                        var (dateTaken, source) = exifService.ExtractDate(filePath);
                        db.Photos.Add(new PhotoRecord
                        {
                            FilePath = filePath,
                            FileName = fileInfo.Name,
                            DateTaken = dateTaken,
                            Month = dateTaken.Month,
                            Day = dateTaken.Day,
                            Year = dateTaken.Year,
                            DateSource = source,
                            FileSize = fileInfo.Length,
                            FileLastModified = fileInfo.LastWriteTimeUtc,
                            IndexedAt = DateTime.UtcNow
                        });
                        newCount++;
                    }

                    // Batch save every 100 records
                    if ((newCount + updatedCount) % 100 == 0)
                    {
                        await db.SaveChangesAsync(ct);
                        _logger.LogInformation("Progress: {New} new, {Updated} updated so far...", newCount, updatedCount);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index file: {File}", filePath);
                }
            }
        }

        await db.SaveChangesAsync(ct);

        // Prune: remove DB entries for files no longer on disk
        var prunedCount = 0;
        var allDbPaths = await db.Photos.Select(p => p.FilePath).ToListAsync(ct);
        foreach (var dbPath in allDbPaths)
        {
            if (!allFilesOnDisk.Contains(dbPath))
            {
                var record = await db.Photos.FirstOrDefaultAsync(p => p.FilePath == dbPath, ct);
                if (record != null)
                {
                    db.Photos.Remove(record);
                    prunedCount++;
                }
            }
        }

        if (prunedCount > 0)
            await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Indexing complete: {New} new, {Updated} updated, {Pruned} pruned. Total: {Total}",
            newCount, updatedCount, prunedCount, await db.Photos.CountAsync(ct));
    }
}
