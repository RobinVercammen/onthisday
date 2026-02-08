using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using OnThisDay.Models;
using OnThisDay.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace OnThisDay.Endpoints;

public static class PhotoEndpoints
{
    private static string? _templateCache;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public static void MapPhotoEndpoints(this WebApplication app)
    {
        app.MapGet("/", HandleHomePage);
        app.MapGet("/photo/{id:int}", HandlePhotoServe);
    }

    private static async Task<IResult> HandleHomePage(
        HttpContext context,
        PhotoQueryService queryService)
    {
        var now = DateTime.Now;
        var month = int.TryParse(context.Request.Query["month"], out var m) ? m : now.Month;
        var day = int.TryParse(context.Request.Query["day"], out var d) ? d : now.Day;

        // Clamp values
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(2000, month)); // use leap year for max days

        var photosByYear = await queryService.GetPhotosForDay(month, day);

        var html = RenderPage(month, day, photosByYear);
        return Results.Content(html, "text/html");
    }

    private static async Task<IResult> HandlePhotoServe(
        int id,
        HttpContext context,
        PhotoQueryService queryService)
    {
        var photo = await queryService.GetPhotoById(id);
        if (photo == null)
            return Results.NotFound();

        if (!File.Exists(photo.FilePath))
            return Results.NotFound();

        // On-the-fly resize for photos when ?w= is specified
        if (photo.MediaType == MediaType.Photo
            && int.TryParse(context.Request.Query["w"], out var width)
            && width > 0 && width <= 2000)
        {
            using var image = await Image.LoadAsync(photo.FilePath);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(width, 0)
            }));

            using var ms = new MemoryStream();
            await image.SaveAsJpegAsync(ms, new JpegEncoder { Quality = 80 });
            return Results.Bytes(ms.ToArray(), "image/jpeg");
        }

        if (!ContentTypeProvider.TryGetContentType(photo.FilePath, out var contentType))
            contentType = "application/octet-stream";

        var stream = File.OpenRead(photo.FilePath);
        return Results.File(stream, contentType, enableRangeProcessing: true);
    }

    private static string RenderPage(int month, int day, Dictionary<int, List<PhotoRecord>> photosByYear)
    {
        var template = GetTemplate();
        var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);

        // Calculate prev/next day
        var currentDate = new DateOnly(2000, month, day); // leap year baseline
        var prevDate = currentDate.AddDays(-1);
        var nextDate = currentDate.AddDays(1);

        var prevLabel = $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(prevDate.Month)} {prevDate.Day}";
        var nextLabel = $"{CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(nextDate.Month)} {nextDate.Day}";

        var content = new StringBuilder();
        var lightboxes = new StringBuilder();

        if (photosByYear.Count == 0)
        {
            content.Append("""
                <div class="empty-state">
                    <h2>No photos found for this day</h2>
                    <p>Photos taken on this date across any year will appear here once indexed.</p>
                </div>
            """);
        }
        else
        {
            foreach (var (year, photos) in photosByYear.OrderByDescending(kv => kv.Key))
            {
                var photoCount = photos.Count(p => p.MediaType == MediaType.Photo);
                var videoCount = photos.Count(p => p.MediaType == MediaType.Video);
                var countParts = new List<string>();
                if (photoCount > 0) countParts.Add($"{photoCount} photo{(photoCount != 1 ? "s" : "")}");
                if (videoCount > 0) countParts.Add($"{videoCount} video{(videoCount != 1 ? "s" : "")}");

                content.Append($"""
                    <section class="year-section">
                        <h2 class="year-header">{year}<span>{string.Join(", ", countParts)}</span></h2>
                        <div class="photo-grid">
                """);

                foreach (var photo in photos)
                {
                    if (photo.MediaType == MediaType.Video)
                    {
                        content.Append($"""
                                <div class="photo-card video-card">
                                    <a href="#lightbox-{photo.Id}">
                                        <div class="video-placeholder">
                                            <svg class="play-icon" viewBox="0 0 24 24" fill="currentColor"><polygon points="5,3 19,12 5,21"/></svg>
                                            <span class="video-filename">{Escape(photo.FileName)}</span>
                                        </div>
                                    </a>
                                </div>
                        """);

                        lightboxes.Append($"""
                            <div id="lightbox-{photo.Id}" class="lightbox">
                                <a href="#" class="lightbox-close">&times;</a>
                                <video src="/photo/{photo.Id}" controls preload="metadata"></video>
                            </div>
                        """);
                    }
                    else
                    {
                        content.Append($"""
                                <div class="photo-card">
                                    <a href="#lightbox-{photo.Id}">
                                        <img src="/photo/{photo.Id}?w=300" alt="{Escape(photo.FileName)}" loading="lazy" />
                                    </a>
                                    <div class="photo-info">{Escape(photo.FileName)}</div>
                                </div>
                        """);

                        lightboxes.Append($"""
                            <div id="lightbox-{photo.Id}" class="lightbox">
                                <a href="#" class="lightbox-close">&times;</a>
                                <img src="/photo/{photo.Id}" alt="{Escape(photo.FileName)}" />
                            </div>
                        """);
                    }
                }

                content.Append("""
                        </div>
                    </section>
                """);
            }
        }

        return template
            .Replace("{{MONTH_NAME}}", monthName)
            .Replace("{{DAY}}", day.ToString())
            .Replace("{{PREV_LINK}}", $"/?month={prevDate.Month}&day={prevDate.Day}")
            .Replace("{{PREV_LABEL}}", prevLabel)
            .Replace("{{NEXT_LINK}}", $"/?month={nextDate.Month}&day={nextDate.Day}")
            .Replace("{{NEXT_LABEL}}", nextLabel)
            .Replace("{{CONTENT}}", content.ToString())
            .Replace("{{LIGHTBOXES}}", lightboxes.ToString());
    }

    private static string GetTemplate()
    {
        if (_templateCache != null)
            return _templateCache;

        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "PhotoPage.html");
        _templateCache = File.ReadAllText(templatePath);
        return _templateCache;
    }

    private static string Escape(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
