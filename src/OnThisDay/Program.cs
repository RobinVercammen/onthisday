using Microsoft.EntityFrameworkCore;
using OnThisDay.Data;
using OnThisDay.Endpoints;
using OnThisDay.Services;

var builder = WebApplication.CreateBuilder(args);

// Add environment variables as configuration source
builder.Configuration.AddEnvironmentVariables();

// Database
var dbPath = builder.Configuration["DATABASE_PATH"] ?? "onthisday.db";
var dbDir = Path.GetDirectoryName(dbPath);
if (!string.IsNullOrEmpty(dbDir))
    Directory.CreateDirectory(dbDir);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Thumbnail cache
builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1_073_741_824; // 1 GB
});

// Services
builder.Services.AddSingleton<ExifService>();
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddScoped<PhotoQueryService>();
builder.Services.AddHostedService<PhotoIndexingService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// Map endpoints
app.MapPhotoEndpoints();

app.Run();
