using System.IO;
using System.IO.Compression;

namespace MangaOrganizer.Services;

public static class AdDetector
{
    private static readonly HashSet<string> ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".avif", ".tiff", ".tif"];

    /// Returns the FullName of the first image entry if it looks like an ad, otherwise null.
    /// Heuristics (either is sufficient):
    ///   • First image has a different extension than the majority of the other images AND is >1.5× the median size
    ///   • First image is >3× the median size of the other images
    public static string? FindSuspectedAd(string cbzPath)
    {
        using var zip = ZipFile.OpenRead(cbzPath);
        var images = zip.Entries
            .Where(e => IsImage(e.Name))
            .OrderBy(e => e.Name)
            .ToList();

        if (images.Count < 2) return null;

        var first = images[0];
        var rest  = images.Skip(1).ToList();

        string firstExt    = Path.GetExtension(first.Name).ToLowerInvariant();
        string majorityExt = rest
            .GroupBy(e => Path.GetExtension(e.Name).ToLowerInvariant())
            .MaxBy(g => g.Count())!.Key;

        var sortedSizes = rest.Select(e => (double)e.Length).Order().ToList();
        double median = sortedSizes.Count % 2 == 0
            ? (sortedSizes[sortedSizes.Count / 2 - 1] + sortedSizes[sortedSizes.Count / 2]) / 2.0
            : sortedSizes[sortedSizes.Count / 2];

        if (median <= 0) return null;

        bool extensionDiffers = firstExt != majorityExt;
        bool sizeOutlier      = first.Length > median * 3.0;
        bool probableAd       = sizeOutlier || (extensionDiffers && first.Length > median * 1.5);

        return probableAd ? first.FullName : null;
    }

    private static bool IsImage(string name) =>
        ImageExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());
}
