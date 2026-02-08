using Microsoft.EntityFrameworkCore;
using OnThisDay.Models;

namespace OnThisDay.Data;

public class AppDbContext : DbContext
{
    public DbSet<PhotoRecord> Photos => Set<PhotoRecord>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PhotoRecord>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.HasIndex(e => new { e.Month, e.Day })
                .HasDatabaseName("IX_Photos_Month_Day");

            entity.HasIndex(e => e.FilePath)
                .IsUnique()
                .HasDatabaseName("IX_Photos_FilePath");

            entity.Property(e => e.FilePath).IsRequired();
            entity.Property(e => e.FileName).IsRequired();
            entity.Property(e => e.DateSource)
                .HasConversion<string>();
            entity.Property(e => e.MediaType)
                .HasConversion<string>();
        });
    }
}
