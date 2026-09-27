# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build MangaOrganizer.sln

# Build release
dotnet build MangaOrganizer.sln -c Release

# Run
dotnet run --project MangaOrganizer/MangaOrganizer.csproj

# Publish self-contained Windows executable
dotnet publish MangaOrganizer/MangaOrganizer.csproj -c Release -r win-x64 --self-contained
```

There are no test projects or lint configs.

## Keeping the README up to date

When you add, remove, or change a user-facing feature — grouping modes, supported filename patterns, supported image formats, UI workflow steps, or build/publish instructions — update [README.md](README.md) to reflect the change. Keep the README accurate; do not add sections for features that don't exist yet.

## Architecture

A Windows-only WPF desktop app (.NET 8, C# 12) with zero NuGet dependencies. All zip/CBZ handling uses `System.IO.Compression` from the BCL.

**Data flow:**

```
MainWindow
  ├─ Scan_Click
  │    ├─ FileParser.Parse(each .cbz)   → List<MangaFile>
  │    └─ FileParser.Group(files, n)    → (GroupMode, List<MergeGroup>)
  │
  └─ Merge_Click (async)
       └─ CbzMerger.MergeAllAsync(groups, outputFolder, progress, ct)
            └─ per group: CbzMerger.MergeGroupAsync(...)
                 → opens each source zip, writes renumbered images → output .cbz
```

**Key files:**

- [MangaOrganizer/Models/MangaFile.cs](MangaOrganizer/Models/MangaFile.cs) — `MangaFile`, `MergeGroup`, `GroupMode` (ByVolume | ByChapterCount)
- [MangaOrganizer/Services/FileParser.cs](MangaOrganizer/Services/FileParser.cs) — regex-based filename parsing; groups by volume number if any file has one, otherwise batches chapters by count
- [MangaOrganizer/Services/CbzMerger.cs](MangaOrganizer/Services/CbzMerger.cs) — extracts images from source CBZ files, renumbers them sequentially (`00001.jpg`, …), writes a new CBZ
- [MangaOrganizer/MainWindow.xaml.cs](MangaOrganizer/MainWindow.xaml.cs) — all UI logic (no MVVM); uses `CancellationToken` + `IProgress<T>` for async merge

**UI:** Code-behind only, no MVVM or data-binding framework. All styles are defined inline in `App.xaml` (dark theme, background `#1E1E2E`, accent `#FF6B35`).
