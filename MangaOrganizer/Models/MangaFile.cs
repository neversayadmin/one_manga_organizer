namespace MangaOrganizer.Models;

public class MangaFile
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public int? Volume { get; set; }
    public double? Chapter { get; set; }
}

public class MergeGroup
{
    public string OutputName { get; set; } = string.Empty;
    public List<MangaFile> Files { get; set; } = [];

    public override string ToString() => $"{OutputName} ({Files.Count} file{(Files.Count == 1 ? "" : "s")})";
}

public enum GroupMode { ByVolume, ByChapterCount }
