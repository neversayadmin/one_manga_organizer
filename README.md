# Manga CBZ Organizer

A Windows desktop app that merges individual manga chapter CBZ files into volume-sized CBZ archives.

## What it does

Point it at a folder of `.cbz` chapter files. It parses filenames to detect series names, volume numbers, and chapter numbers, then groups the chapters and merges each group into a single output CBZ — renumbering pages sequentially across all source files.

**Grouping modes** (chosen automatically, or overridden):
- **By Volume** — when filenames contain volume numbers (`Vol.1`, `v01`, etc.), chapters are grouped per volume
- **By Chapter Count** — when no volume numbers are found, chapters are batched in configurable groups (default: 10 chapters per output file)
- **Single File** — check "Merge into single CBZ" after scanning to combine everything into one output file (`Series Name Complete.cbz`)

## Requirements

- Windows 10/11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or use the self-contained build below)

## Building

```bash
# Debug build
dotnet build MangaOrganizer.sln

# Self-contained single-folder publish (no .NET install required on target machine)
dotnet publish MangaOrganizer/MangaOrganizer.csproj -c Release -r win-x64 --self-contained
```

## Usage

1. Click **Browse** to select the folder containing your `.cbz` chapter files
2. Click **Scan** — the app detects grouping mode and previews all merge groups in a tree
3. Optionally adjust **Chapters per group** (only visible in chapter-count mode)
4. Select an output folder (defaults to a `Merged/` subfolder inside the source folder)
5. Click **Merge All** — progress is shown per group; merge can be cancelled mid-run

On completion, you can open the output folder directly from the success dialog.

## Filename formats recognized

The parser handles common manga filename patterns for both volumes and chapters:

| Pattern | Examples |
|---------|---------|
| Volume | `Vol.1`, `Vol 01`, `Volume 1`, `v01` |
| Chapter | `Ch.001`, `Ch 1`, `Chapter 1`, `c01`, `#001`, `9.5` (decimal chapters) |

The series name is extracted from everything before the first volume/chapter marker.

## Supported image formats

CBZ contents: `.jpg`, `.png`, `.gif`, `.webp`, `.bmp`, `.avif`, `.tiff`

## Tech stack

- C# 12 / .NET 8 / WPF
- No third-party dependencies — zip handling via `System.IO.Compression`
- Windows-only (`net8.0-windows`)
