# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run Commands

```bash
# Build
dotnet build src/OnThisDay/OnThisDay.csproj

# Run (dev - serves on http://localhost:5239)
dotnet run --project src/OnThisDay/OnThisDay.csproj

# Publish release
dotnet publish src/OnThisDay/OnThisDay.csproj -c Release -o ./publish

# Docker
docker build -t onthisday:latest .
docker-compose up
```

There are no tests or linting configured in this project.

## Architecture

**OnThisDay** is a self-hosted photo timeline viewer that shows photos taken on today's calendar date across all years. It's a single ASP.NET Core (.NET 10) application with a vanilla JS frontend — no frameworks on either side.

### Data Flow

1. **PhotoIndexingService** (background hosted service) scans configured directories on startup and every N hours, extracts EXIF dates in parallel via **ExifService**, and batch-upserts **PhotoRecord** entries into SQLite via EF Core.
2. **PhotoEndpoints** serves a single HTML page (`GET /`) with photo metadata embedded as JSON, plus media endpoints (`GET /photo/{id}`, `/photo/{id}/live`).
3. **PhotoPage.html** (the entire frontend) uses two-level virtual scrolling — section-level (year groups) and card-level (individual photos) — via IntersectionObserver to handle large libraries without DOM bloat.

### Key Source Layout

- `src/OnThisDay/Program.cs` — DI setup and app startup (minimal APIs, no controllers)
- `src/OnThisDay/Services/PhotoIndexingService.cs` — Background scanner with parallel EXIF extraction, deduplication across overlapping directories, Live Photo detection, change detection via file size + mtime
- `src/OnThisDay/Services/ExifService.cs` — EXIF date extraction (DateTimeOriginal → DateTime → file system fallback)
- `src/OnThisDay/Services/PhotoQueryService.cs` — Queries photos by month/day, groups by year
- `src/OnThisDay/Data/AppDbContext.cs` — SQLite schema with compound index on (Month, Day) and unique index on FilePath
- `src/OnThisDay/Endpoints/PhotoEndpoints.cs` — HTTP routes, template rendering via string replacement, range request support, aggressive caching headers
- `src/OnThisDay/Templates/PhotoPage.html` — Server-rendered template containing all HTML/CSS/JS (no build step)
- `src/OnThisDay/Models/PhotoRecord.cs` — Data model with MediaType and DateSource enums

### Configuration (Environment Variables)

| Variable | Default | Description |
|---|---|---|
| `PHOTO_DIRECTORIES` | (required) | Semicolon-delimited photo directory paths |
| `DATABASE_PATH` | `onthisday.db` | SQLite database file path |
| `RESCAN_INTERVAL_HOURS` | `6` | Background scan interval |

### Notable Design Decisions

- **No templating engine**: PhotoEndpoints uses string replacement on the cached HTML template
- **Producer-consumer indexing**: Parallel EXIF reads feed an unbounded channel; a single consumer writes to the DB in batches of 500
- **Frontend is a single HTML file**: All CSS/JS is inline in PhotoPage.html — no bundler, no node_modules
- **Thumbnail caching**: Low-res thumbnail data URLs and video poster frames are cached client-side to avoid re-fetching on scroll
- **Live Photo support**: Detects .mov sibling files during indexing and associates them with their parent image
