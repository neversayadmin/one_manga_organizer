using System.IO;
using System.IO.Compression;
using MangaOrganizer.Models;

namespace MangaOrganizer.Services;

public static class CbzMerger
{
    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".tiff", ".tif"];

    public static async Task MergeGroupAsync(
        MergeGroup group,
        string outputFolder,
        IProgress<(int current, int total, string status)>? progress = null,
        CancellationToken ct = default)
    {
        string outputPath = Path.Combine(outputFolder, group.OutputName + ".cbz");

        // Collect all entries first to know total page count
        var allEntries = new List<(string SourceZip, string EntryName)>();
        foreach (var file in group.Files)
        {
            using var zip = ZipFile.OpenRead(file.FilePath);
            foreach (var entry in zip.Entries
                .Where(e => IsImage(e.Name) && e.FullName != file.SuspectedAdEntry)
                .OrderBy(e => e.Name))
                allEntries.Add((file.FilePath, entry.FullName));
        }

        int total = allEntries.Count;
        int current = 0;

        using var outputStream = File.Create(outputPath);
        using var outputZip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false);

        // Group by source zip to avoid reopening repeatedly
        foreach (var file in group.Files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((current, total, $"Processing {Path.GetFileName(file.FilePath)}..."));

            using var inputZip = ZipFile.OpenRead(file.FilePath);
            var imageEntries = inputZip.Entries
                .Where(e => IsImage(e.Name) && e.FullName != file.SuspectedAdEntry)
                .OrderBy(e => e.Name)
                .ToList();

            foreach (var entry in imageEntries)
            {
                ct.ThrowIfCancellationRequested();
                current++;
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                string newName = $"{current:D5}{ext}";

                var newEntry = outputZip.CreateEntry(newName, CompressionLevel.Fastest);
                newEntry.LastWriteTime = DateTimeOffset.UtcNow;

                using var src = entry.Open();
                using var dst = newEntry.Open();
                await src.CopyToAsync(dst, ct);

                progress?.Report((current, total, $"Page {current}/{total}"));
            }
        }
    }

    public static async Task MergeAllAsync(
        List<MergeGroup> groups,
        string outputFolder,
        IProgress<(int groupIndex, int groupTotal, int pageIndex, int pageTotal, string status)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        for (int g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[g];

            var pageProgress = new Progress<(int current, int total, string status)>(p =>
                progress?.Report((g + 1, groups.Count, p.current, p.total, p.status)));

            await MergeGroupAsync(group, outputFolder, pageProgress, ct);
        }
    }

    private static bool IsImage(string name) =>
        ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());
}
