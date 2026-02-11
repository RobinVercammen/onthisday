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
        var currentYear = DateTime.Now.Year;
        var photos = await _db.Photos
            .Where(p => p.Month == month && p.Day == day && p.Year < currentYear && p.FileHash != "")
            .OrderByDescending(p => p.Year)
            .ThenBy(p => p.DateTaken)
            .AsNoTracking()
            .ToListAsync();

        return photos
            .GroupBy(p => p.Year)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public async Task<PhotoRecord?> GetPhotoByHash(string fileHash)
    {
        return await _db.Photos
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.FileHash == fileHash);
    }
}
