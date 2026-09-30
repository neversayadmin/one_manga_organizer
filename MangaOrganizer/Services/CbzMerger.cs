using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using MangaOrganizer.Models;

namespace MangaOrganizer.Services;

public static class CbzMerger
{
    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".tiff", ".tif"];

    public static async Task<int> MergeGroupAsync(
        MergeGroup group,
        string outputFolder,
        bool skipAds,
        int? jpegQuality = null,
        IProgress<(int current, int total, string status)>? progress = null,
        CancellationToken ct = default)
    {
        string outputPath = Path.Combine(outputFolder, group.OutputName + ".cbz");

        bool IsIncluded(ZipArchiveEntry e, MangaFile f) =>
            IsImage(e.Name) && (!skipAds || !f.SuspectedAdEntries.Contains(e.FullName));

        if (jpegQuality.HasValue)
            return await MergeGroupWithCompressionAsync(group, outputPath, IsIncluded, jpegQuality.Value, progress, ct);

        await MergeGroupDirectAsync(group, outputPath, IsIncluded, progress, ct);
        return 0;
    }

    // No compression: stream directly from source ZIPs to output ZIP.
    private static async Task MergeGroupDirectAsync(
        MergeGroup group,
        string outputPath,
        Func<ZipArchiveEntry, MangaFile, bool> isIncluded,
        IProgress<(int current, int total, string status)>? progress,
        CancellationToken ct)
    {
        // First pass: count total pages for accurate progress
        int total = 0;
        foreach (var file in group.Files)
        {
            using var zip = ZipFile.OpenRead(file.FilePath);
            total += zip.Entries.Count(e => isIncluded(e, file));
        }

        int current = 0;
        using var outputStream = File.Create(outputPath);
        using var outputZip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false);

        foreach (var file in group.Files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((current, total, $"Processing {Path.GetFileName(file.FilePath)}…"));

            using var inputZip = ZipFile.OpenRead(file.FilePath);
            foreach (var entry in inputZip.Entries.Where(e => isIncluded(e, file)).OrderBy(e => e.Name))
            {
                ct.ThrowIfCancellationRequested();
                current++;
                string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                var newEntry = outputZip.CreateEntry($"{current:D5}{ext}", CompressionLevel.Fastest);
                newEntry.LastWriteTime = DateTimeOffset.UtcNow;
                using var src = entry.Open();
                using var dst = newEntry.Open();
                await src.CopyToAsync(dst, ct);
                progress?.Report((current, total, $"Page {current}/{total}"));
            }
        }
    }

    // With JPEG compression: read all pages to memory, compress in parallel, write sequentially.
    // Returns the number of images that could not be compressed and were kept as-is.
    private static async Task<int> MergeGroupWithCompressionAsync(
        MergeGroup group,
        string outputPath,
        Func<ZipArchiveEntry, MangaFile, bool> isIncluded,
        int quality,
        IProgress<(int current, int total, string status)>? progress,
        CancellationToken ct)
    {
        // Collect ordered (sourceZip, entryFullName) pairs
        var entries = new List<(string SourceZip, string FullName)>();
        foreach (var file in group.Files)
        {
            using var zip = ZipFile.OpenRead(file.FilePath);
            foreach (var e in zip.Entries.Where(e => isIncluded(e, file)).OrderBy(e => e.Name))
                entries.Add((file.FilePath, e.FullName));
        }

        int total = entries.Count;
        progress?.Report((0, total, "Reading pages…"));

        // Read all raw image bytes sequentially (one ZIP at a time)
        var rawBytes = new byte[total][];
        int idx = 0;
        foreach (var fileGroup in entries.GroupBy(e => e.SourceZip))
        {
            using var zip = ZipFile.OpenRead(fileGroup.Key);
            foreach (var (_, fullName) in fileGroup)
            {
                ct.ThrowIfCancellationRequested();
                var e = zip.GetEntry(fullName)!;
                using var ms = new MemoryStream();
                using (var s = e.Open()) await s.CopyToAsync(ms, ct);
                rawBytes[idx++] = ms.ToArray();
            }
        }

        // Compress all pages in parallel (CPU-bound, thread-safe)
        var compressed = new byte[total][];
        int done = 0;
        int failed = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, total),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (i, _) =>
            {
                bool ok;
                (compressed[i], ok) = CompressToJpeg(new MemoryStream(rawBytes[i]), quality);
                if (!ok) Interlocked.Increment(ref failed);
                rawBytes[i] = []; // release raw memory as we go
                int n = Interlocked.Increment(ref done);
                progress?.Report((n, total, $"Compressing {n}/{total}…"));
                return ValueTask.CompletedTask;
            });

        // Write sequentially (ZipArchive is not thread-safe for writes)
        using var outputStream = File.Create(outputPath);
        using var outputZip = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false);
        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var newEntry = outputZip.CreateEntry($"{i + 1:D5}.jpg", CompressionLevel.Fastest);
            newEntry.LastWriteTime = DateTimeOffset.UtcNow;
            using var dst = newEntry.Open();
            await dst.WriteAsync(compressed[i], ct);
        }

        return failed;
    }

    public static async Task<int> MergeAllAsync(
        List<MergeGroup> groups,
        string outputFolder,
        bool skipAds,
        int? jpegQuality = null,
        IProgress<(int pagesCompleted, int pagesTotal, string status)>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        // These are only read/written from Progress callbacks, which are all posted to the
        // UI SynchronizationContext and therefore run serially — no locking needed.
        int totalPages = 0;
        int completedPages = 0;
        int totalFailed = 0;

        int parallelGroups = Math.Max(1, Environment.ProcessorCount / 2);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, groups.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelGroups, CancellationToken = ct },
            async (g, ct) =>
            {
                var group = groups[g];
                bool totalAdded = false;
                int lastCompleted = 0;

                var pageProgress = new Progress<(int current, int total, string status)>(p =>
                {
                    if (!totalAdded && p.total > 0) { totalPages += p.total; totalAdded = true; }
                    int delta = p.current - lastCompleted;
                    completedPages += delta;
                    lastCompleted = p.current;
                    progress?.Report((completedPages, totalPages, p.status));
                });

                int failed = await MergeGroupAsync(group, outputFolder, skipAds, jpegQuality, pageProgress, ct);
                Interlocked.Add(ref totalFailed, failed);
            });

        return totalFailed;
    }

    // Returns (compressed bytes, true) on success, (original bytes, false) on failure.
    private static (byte[] Data, bool Compressed) CompressToJpeg(Stream imageStream, int quality)
    {
        using var raw = new MemoryStream();
        imageStream.CopyTo(raw);
        byte[] rawBytes = raw.ToArray();

        try
        {
            raw.Position = 0;
            var decoder = BitmapDecoder.Create(raw, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (ms.ToArray(), true);
        }
        catch
        {
            return (rawBytes, false);
        }
    }

    private static bool IsImage(string name) =>
        ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());
}
