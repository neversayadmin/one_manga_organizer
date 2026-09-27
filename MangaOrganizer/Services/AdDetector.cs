using System.IO;
using System.IO.Compression;

namespace MangaOrganizer.Services;

public static class AdDetector
{
    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".tiff", ".tif"];

    // A border image (first or last) is a suspected ad when:
    //   1. Its extension differs from the majority extension of the interior pages, AND
    //   2. Its aspect ratio is portrait or square (w/h ≤ 1.3)
    //      — landscape means a double-page colour spread, which is legitimate content.
    // Returns the FullNames of any suspected entries (0, 1, or 2 items).
    public static HashSet<string> FindSuspectedAds(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        var images = zip.Entries
            .Where(e => IsImage(e.Name))
            .OrderBy(e => e.Name)
            .ToList();

        if (images.Count < 3) return [];

        var interior = images[1..^1];

        string majorityExt = interior
            .GroupBy(e => Path.GetExtension(e.Name).ToLowerInvariant())
            .MaxBy(g => g.Count())!.Key;

        var suspects = new HashSet<string>();

        if (IsSuspect(images[0], majorityExt))
            suspects.Add(images[0].FullName);

        if (IsSuspect(images[^1], majorityExt))
            suspects.Add(images[^1].FullName);

        return suspects;
    }

    private static bool IsSuspect(ZipArchiveEntry entry, string majorityExt)
    {
        string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
        if (ext == majorityExt) return false;     // same format as interior → not suspicious
        return !IsLandscape(entry);               // portrait/square = single page = likely ad
    }

    // Returns true if the image is wider than it is tall (double-page spread).
    // Reads only the image header — no full decode.
    private static bool IsLandscape(ZipArchiveEntry entry)
    {
        try
        {
            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            using var s = entry.Open();
            var (w, h) = ext is ".png" ? ReadPngDims(s) : ReadJpegDims(s);
            return w > 0 && h > 0 && w > h * 1.3;
        }
        catch { return false; }
    }

    private static (int W, int H) ReadPngDims(Stream s)
    {
        var buf = new byte[24];
        if (ReadFull(s, buf) < 24) return (0, 0);
        // PNG: 8-byte signature + IHDR = 4 len + 4 "IHDR" + 4 width + 4 height
        return (
            (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19],
            (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23]
        );
    }

    private static (int W, int H) ReadJpegDims(Stream s)
    {
        // Read up to 64 KB — SOF marker is always within the header for typical manga scans.
        var buf = new byte[65536];
        int total = ReadFull(s, buf);

        // Scan for any SOF marker (0xFFCx where x ∈ {0-3, 5-7, 9-B, D-F})
        for (int i = 2; i < total - 8; i++)
        {
            if (buf[i] != 0xFF) continue;
            byte m = buf[i + 1];
            if (m is 0xC0 or 0xC1 or 0xC2 or 0xC3
                   or 0xC5 or 0xC6 or 0xC7
                   or 0xC9 or 0xCA or 0xCB
                   or 0xCD or 0xCE or 0xCF)
            {
                // SOF payload: [2-byte len] [1-byte precision] [2-byte height] [2-byte width]
                return (
                    (buf[i + 7] << 8) | buf[i + 8],   // width
                    (buf[i + 5] << 8) | buf[i + 6]    // height
                );
            }
        }
        return (0, 0);
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int total = 0, n;
        while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0)
            total += n;
        return total;
    }

    private static bool IsImage(string name) =>
        ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());
}
