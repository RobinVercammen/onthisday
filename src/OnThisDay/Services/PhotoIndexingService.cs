using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

    // Person detection only covers photos from this year on; older ones are never scanned
    private const int DetectMinYear = 2023;
    private const int DetectBatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PhotoIndexingService> _logger;
    private readonly ThumbnailService _thumbnailService;
    private readonly PersonDetector _personDetector;
    private readonly List<string> _photoDirectories;
    private readonly TimeSpan _rescanInterval;

    public PhotoIndexingService(
        IServiceScopeFactory scopeFactory,
        ILogger<PhotoIndexingService> logger,
        ThumbnailService thumbnailService,
        PersonDetector personDetector,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _thumbnailService = thumbnailService;
        _personDetector = personDetector;

        var dirs = configuration["PHOTO_DIRECTORIES"] ?? "";
        _photoDirectories = dirs
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var hours = double.TryParse(configuration["RESCAN_INTERVAL_HOURS"], out var h) ? h : 6;
        _rescanInterval = TimeSpan.FromHours(hours);
    }

    private static string ComputeFileHash(string filePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(filePath));
        return Convert.ToBase64String(hash, 0, 9)
            .Replace('+', '-')
            .Replace('/', '_');
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

                var today = DateTime.Now;
                await _thumbnailService.PreloadThumbnailsForDayAsync(today.Month, today.Day, stoppingToken);

                await DetectPersonsAsync(stoppingToken);
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
            .GroupBy(p => p.FilePath)
            .Select(g => g.First())
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

        // Collect all files to index, deduplicating across overlapping directories
        var allFilesOnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                allFilesOnDisk.Add(filePath);
            }
        }

        var filesToIndex = allFilesOnDisk.ToList();
        _logger.LogInformation("Found {Count} files to index", filesToIndex.Count);

        // Extract EXIF data in parallel using a producer-consumer pattern
        var channel = Channel.CreateUnbounded<(string filePath, FileInfo fileInfo, DateTime dateTaken, DateSource source, MediaType mediaType, string? movSibling, string fileHash)>(
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
                    var fileHash = ComputeFileHash(filePath);

                    // Skip unchanged files early (but force reprocess if FileHash is empty from pre-upgrade DB)
                    if (existingRecords.TryGetValue(filePath, out var existing)
                        && existing.FileSize == fileInfo.Length
                        && existing.FileLastModified == fileInfo.LastWriteTimeUtc
                        && !string.IsNullOrEmpty(existing.FileHash))
                    {
                        return;
                    }

                    var (dateTaken, source) = exifService.ExtractDate(filePath);
                    var mediaType = ExifService.GetMediaType(filePath);
                    var movSibling = mediaType == MediaType.Photo ? FindMovSibling(filePath) : null;

                    channel.Writer.TryWrite((filePath, fileInfo, dateTaken, source, mediaType, movSibling, fileHash));
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
                existing.FileHash = item.fileHash;

                // Changed file: scan it for persons again
                existing.PersonScore = null;
                existing.PersonArea = null;
                existing.FocusX = null;
                existing.FocusY = null;
                existing.FocusW = null;
                existing.FocusH = null;
                updatedCount++;
            }
            else
            {
                db.Photos.Add(new PhotoRecord
                {
                    FilePath = item.filePath,
                    FileHash = item.fileHash,
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

    private async Task DetectPersonsAsync(CancellationToken ct)
    {
        if (!_personDetector.IsEnabled)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        IQueryable<PhotoRecord> Pending() => db.Photos
            .Where(p => p.MediaType == MediaType.Photo && p.Year >= DetectMinYear && p.PersonScore == null);

        var total = await Pending().CountAsync(ct);
        if (total == 0)
        {
            _logger.LogInformation("Person detection: nothing to scan");
            return;
        }

        var scanned = 0;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            // Pick each batch afresh: today's and tomorrow's photos go first (the hubcast night fetch wants them),
            // also when the backfill runs past midnight; the rest follows oldest IndexedAt first
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);
            int m1 = today.Month, d1 = today.Day, m2 = tomorrow.Month, d2 = tomorrow.Day;

            if (scanned == 0)
            {
                var priority = await Pending().CountAsync(p => (p.Month == m1 && p.Day == d1) || (p.Month == m2 && p.Day == d2), ct);
                _logger.LogInformation("Person detection: {Total} photos to scan, {Priority} of them from today or tomorrow",
                    total, priority);
            }

            var batch = await Pending()
                .OrderByDescending(p => (p.Month == m1 && p.Day == d1) || (p.Month == m2 && p.Day == d2))
                .ThenBy(p => p.IndexedAt)
                .Take(DetectBatchSize)
                .ToListAsync(ct);

            if (batch.Count == 0)
                break;

            foreach (var photo in batch)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var result = _personDetector.Detect(photo.FilePath);
                    photo.PersonScore = result.Score;
                    photo.PersonArea = result.Area;
                    photo.FocusX = result.Focus?.X;
                    photo.FocusY = result.Focus?.Y;
                    photo.FocusW = result.Focus?.W;
                    photo.FocusH = result.Focus?.H;
                }
                catch (Exception ex)
                {
                    // Score 0 so a broken file is not retried on every scan
                    _logger.LogWarning(ex, "Person detection failed for {File}", photo.FilePath);
                    photo.PersonScore = 0;
                    photo.PersonArea = 0;
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            scanned += batch.Count;

            _logger.LogInformation("Person detection: {Scanned}/{Total} scanned, {Ms} ms per photo",
                scanned, total, stopwatch.ElapsedMilliseconds / scanned);
        }
    }
}
