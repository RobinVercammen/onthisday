namespace OnThisDay.Models;

public enum DateSource
{
    ExifDateTimeOriginal,
    ExifDateTime,
    FileSystem
}

public class PhotoRecord
{
    public int Id { get; set; }
    public required string FilePath { get; set; }
    public required string FileName { get; set; }
    public DateTime DateTaken { get; set; }
    public int Month { get; set; }
    public int Day { get; set; }
    public int Year { get; set; }
    public DateSource DateSource { get; set; }
    public long FileSize { get; set; }
    public DateTime FileLastModified { get; set; }
    public DateTime IndexedAt { get; set; }
}
