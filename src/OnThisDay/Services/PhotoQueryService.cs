using Microsoft.EntityFrameworkCore;
using OnThisDay.Data;
using OnThisDay.Models;

namespace OnThisDay.Services;

public class PhotoQueryService
{
    private readonly AppDbContext _db;

    public PhotoQueryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Dictionary<int, List<PhotoRecord>>> GetPhotosForDay(int month, int day)
    {
        var photos = await _db.Photos
            .Where(p => p.Month == month && p.Day == day && p.FileHash != "")
            .OrderByDescending(p => p.Year)
            .ThenBy(p => p.DateTaken)
            .AsNoTracking()
            .ToListAsync();

        return WithoutDuplicates(photos)
            .GroupBy(p => p.Year)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<List<PhotoRecord>> GetPhotosForDay(int month, int day, int minYear, bool photosOnly)
    {
        var photos = await _db.Photos
            .Where(p => p.Month == month && p.Day == day && p.Year >= minYear && p.FileHash != "")
            .Where(p => !photosOnly || p.MediaType == MediaType.Photo)
            .OrderByDescending(p => p.Year)
            .ThenBy(p => p.DateTaken)
            .AsNoTracking()
            .ToListAsync();

        return WithoutDuplicates(photos);
    }

    // The same file copied into several libraries (or folders) keeps its capture time and size
    public static (DateTime, long, MediaType) DuplicateKey(PhotoRecord photo) =>
        (photo.DateTaken, photo.FileSize, photo.MediaType);

    // Per day, in memory: one pass keeps the first copy in place, swapped for a later copy that has a Live Photo video
    private static List<PhotoRecord> WithoutDuplicates(List<PhotoRecord> photos)
    {
        var result = new List<PhotoRecord>(photos.Count);
        var slots = new Dictionary<(DateTime, long, MediaType), int>(photos.Count);
        foreach (var photo in photos)
        {
            var key = DuplicateKey(photo);
            if (!slots.TryAdd(key, result.Count))
            {
                var slot = slots[key];
                if (result[slot].LivePhotoMovPath == null && photo.LivePhotoMovPath != null)
                    result[slot] = photo;
                continue;
            }
            result.Add(photo);
        }
        return result;
    }

    public async Task<PhotoRecord?> GetPhotoByHash(string fileHash)
    {
        return await _db.Photos
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.FileHash == fileHash);
    }
}
