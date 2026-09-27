using System.Runtime.InteropServices;

namespace MangaOrganizer.Services;

internal sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string x, string y);

    public int Compare(string? x, string? y) =>
        StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);
}
