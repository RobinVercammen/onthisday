using System.Globalization;
using System.Text;
using ImageMagick;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using OnThisDay.Models;
using OnThisDay.Services;

namespace OnThisDay.Endpoints;

public static class PhotoEndpoints
{
    private static string? _templateCache;
    private static byte[]? _appleTouchIconCache;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    public static void MapPhotoEndpoints(this WebApplication app)
    {
        app.MapGet("/", HandleHomePage);
        app.MapGet("/photo/{id:int}", HandlePhotoServe);
        app.MapGet("/photo/{id:int}/live", HandleLivePhotoServe);
        app.MapGet("/apple-touch-icon.png", HandleAppleTouchIcon);
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

        if (!ContentTypeProvider.TryGetContentType(photo.FilePath, out var contentType))
            contentType = "application/octet-stream";

        var fileInfo = new FileInfo(photo.FilePath);
        var lastModified = new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero);
        var eTag = new EntityTagHeaderValue($"\"{id}-{fileInfo.Length}\"");

        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";

        return Results.File(
            photo.FilePath,
            contentType,
            lastModified: lastModified,
            entityTag: eTag,
            enableRangeProcessing: true);
    }

    private static async Task<IResult> HandleLivePhotoServe(
        int id,
        HttpContext context,
        PhotoQueryService queryService)
    {
        var photo = await queryService.GetPhotoById(id);
        if (photo?.LivePhotoMovPath == null || !File.Exists(photo.LivePhotoMovPath))
            return Results.NotFound();

        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(photo.LivePhotoMovPath, "video/quicktime", enableRangeProcessing: true);
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
            // Embed photo data as JSON for client-side virtual scrolling
            content.Append("<script>window.__photoData = [");
            var first = true;
            foreach (var (year, photos) in photosByYear.OrderByDescending(kv => kv.Key))
            {
                if (!first) content.Append(',');
                first = false;
                content.Append($"{{\"year\":{year},\"items\":[");
                for (var i = 0; i < photos.Count; i++)
                {
                    if (i > 0) content.Append(',');
                    var p = photos[i];
                    var type = p.MediaType == MediaType.Video ? "video" : "photo";
                    var live = p.LivePhotoMovPath != null ? ",\"live\":true" : "";
                    content.Append($"{{\"id\":{p.Id},\"type\":\"{type}\",\"name\":\"{EscapeJson(p.FileName)}\"{live}}}");
                }
                content.Append("]}");
            }
            content.Append("];</script>");

            // Render year section shells without card divs
            foreach (var (year, photos) in photosByYear.OrderByDescending(kv => kv.Key))
            {
                var photoCount = photos.Count(p => p.MediaType == MediaType.Photo);
                var videoCount = photos.Count(p => p.MediaType == MediaType.Video);
                var countParts = new List<string>();
                if (photoCount > 0) countParts.Add($"{photoCount} photo{(photoCount != 1 ? "s" : "")}");
                if (videoCount > 0) countParts.Add($"{videoCount} video{(videoCount != 1 ? "s" : "")}");

                content.Append($"""
                    <section class="year-section" data-year="{year}">
                        <h2 class="year-header">{year}<span>{string.Join(", ", countParts)}</span></h2>
                        <div class="photo-grid"></div>
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
            .Replace("{{CONTENT}}", content.ToString());
    }

    private const string IconSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 180 180">
            <rect width="180" height="180" rx="28" fill="#0a0a0a"/>
            <rect x="30" y="44" width="120" height="106" rx="12" fill="none" stroke="#e0e0e0" stroke-width="6"/>
            <rect x="30" y="44" width="120" height="30" rx="12" fill="#e0e0e0"/>
            <circle cx="60" cy="36" r="8" fill="#e0e0e0"/>
            <circle cx="120" cy="36" r="8" fill="#e0e0e0"/>
            <line x1="60" y1="28" x2="60" y2="44" stroke="#e0e0e0" stroke-width="6" stroke-linecap="round"/>
            <line x1="120" y1="28" x2="120" y2="44" stroke="#e0e0e0" stroke-width="6" stroke-linecap="round"/>
            <circle cx="100" cy="115" r="22" fill="none" stroke="#8ab4f8" stroke-width="5"/>
            <circle cx="100" cy="115" r="8" fill="#8ab4f8"/>
            <rect x="82" y="97" width="36" height="6" rx="3" fill="#8ab4f8" transform="rotate(-45 100 100)"/>
        </svg>
        """;

    private static IResult HandleAppleTouchIcon(HttpContext context)
    {
        if (_appleTouchIconCache == null)
        {
            using var image = new MagickImage(Encoding.UTF8.GetBytes(IconSvg), MagickFormat.Svg);
            image.Resize(180, 180);
            image.Format = MagickFormat.Png;
            _appleTouchIconCache = image.ToByteArray();
        }

        context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return Results.File(_appleTouchIconCache, "image/png");
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

    private static string EscapeJson(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }
}
