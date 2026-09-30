using System.IO;
using System.Text.RegularExpressions;
using MangaOrganizer.Models;

namespace MangaOrganizer.Services;

public static class FileParser
{
    // Matches: Vol.1, Vol 01, Volume 1, v01 (word-boundary, case-insensitive)
    private static readonly Regex VolumeRegex = new(
        @"(?:vol(?:ume)?\.?\s*|(?<![a-zA-Z])v(?=[0-9]))(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches: Ch.001, Ch 1, Chapter 1, c01, #001, Capitolo 1, Cap.1, Capítulo 1 — decimal part preserved (e.g. 9.5)
    private static readonly Regex ChapterRegex = new(
        @"(?:ch(?:apter)?\.?\s*|cap(?:itol[ao]|[íi]tulo)?\.?\s*|(?<![a-zA-Z])c(?=[0-9])|#)(\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Parses a .cbz filename into a MangaFile.
    /// <paramref name="fallbackSeriesName"/> is used when no series prefix is found
    /// (e.g. files start directly with "Vol.1 Ch.1 - ...").
    /// </summary>
    public static MangaFile Parse(string filePath, string fallbackSeriesName = "")
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        // Replace underscores but keep dots — decimal chapters use them (e.g. Ch.9.5)
        string searchStr = name.Replace('_', ' ');

        var volMatch = VolumeRegex.Match(searchStr);
        var chMatch = ChapterRegex.Match(searchStr);

        int? volume = volMatch.Success ? int.Parse(volMatch.Groups[1].Value) : null;
        double? chapter = null;
        if (chMatch.Success)
        {
            string chStr = chMatch.Groups[1].Value.Replace(',', '.');
            chapter = double.Parse(chStr, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Series name = everything before the first marker
        int firstMarker = searchStr.Length;
        if (volMatch.Success) firstMarker = Math.Min(firstMarker, volMatch.Index);
        if (chMatch.Success) firstMarker = Math.Min(firstMarker, chMatch.Index);

        // Now safe to replace dots with spaces for display (markers already found)
        string seriesName = searchStr[..firstMarker]
            .Replace('.', ' ')
            .Trim(' ', '-', '_', '[', ']', '(', ')');

        if (string.IsNullOrWhiteSpace(seriesName))
            seriesName = string.IsNullOrWhiteSpace(fallbackSeriesName) ? "Unknown Series" : fallbackSeriesName;

        return new MangaFile
        {
            FilePath = filePath,
            FileName = name,
            SeriesName = seriesName,
            Volume = volume,
            Chapter = chapter
        };
    }

    public static (GroupMode Mode, List<MergeGroup> Groups) Group(
        List<MangaFile> files, int chaptersPerGroup = 10)
    {
        bool hasVolumes = files.Any(f => f.Volume.HasValue);

        if (hasVolumes)
        {
            var groups = files
                .GroupBy(f => (Series: f.SeriesName, Vol: f.Volume ?? 0))
                .OrderBy(g => g.Key.Series)
                .ThenBy(g => g.Key.Vol)
                .Select(g =>
                {
                    var ordered = g.OrderBy(f => f.Chapter ?? 0).ThenBy(f => f.FileName, NaturalComparer.Instance).ToList();
                    var first = ordered.First();
                    var last = ordered.Last();
                    string outputName = (first.Chapter.HasValue && last.Chapter.HasValue)
                        ? $"{g.Key.Series} - {(int)first.Chapter}_{(int)last.Chapter}"
                        : $"{g.Key.Series} - Vol.{g.Key.Vol:D2}";
                    return new MergeGroup
                    {
                        OutputName = SanitizeName(outputName),
                        Files = ordered
                    };
                })
                .ToList();

            return (GroupMode.ByVolume, groups);
        }
        else
        {
            var sorted = files
                .OrderBy(f => f.Chapter ?? double.MaxValue)
                .ThenBy(f => f.FileName, NaturalComparer.Instance)
                .ToList();

            var groups = new List<MergeGroup>();
            for (int i = 0; i < sorted.Count; i += chaptersPerGroup)
            {
                var batch = sorted.Skip(i).Take(chaptersPerGroup).ToList();
                var first = batch.First();
                var last = batch.Last();

                string outputName = (first.Chapter.HasValue && last.Chapter.HasValue)
                    ? $"{first.SeriesName} - {(int)first.Chapter}_{(int)last.Chapter}"
                    : $"{first.SeriesName} - Part {i / chaptersPerGroup + 1:D2}";

                groups.Add(new MergeGroup
                {
                    OutputName = SanitizeName(outputName),
                    Files = batch
                });
            }

            return (GroupMode.ByChapterCount, groups);
        }
    }

    internal static string SanitizeName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
