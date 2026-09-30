using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MangaOrganizer.Models;
using MangaOrganizer.Services;

namespace MangaOrganizer;

public partial class MainWindow : Window
{
    private List<MangaFile> _parsedFiles = [];
    private List<MergeGroup> _groups = [];
    private GroupMode _mode = GroupMode.ByVolume;
    private CancellationTokenSource? _cts;

    private static readonly string LastFolderFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MangaOrganizer", "last_folder.txt");

    public MainWindow()
    {
        InitializeComponent();
        SourceFolderBox.TextChanged += (_, _) =>
        {
            UpdateScanButton();
            string src = SourceFolderBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(src) &&
                (string.IsNullOrWhiteSpace(OutputFolderBox.Text) ||
                 OutputFolderBox.Text.TrimEnd('\\', '/').EndsWith("Merged", StringComparison.OrdinalIgnoreCase)))
            {
                OutputFolderBox.Text = Path.Combine(src, "Merged");
            }
        };
        LoadLastFolder();
    }

    // ── Folder Browse ────────────────────────────────────────────────

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        string? folder = PickFolder("Select source folder with .cbz files");
        if (folder is null) return;
        SourceFolderBox.Text = folder;

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
            OutputFolderBox.Text = Path.Combine(folder, "Merged");
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        string? folder = PickFolder("Select output folder");
        if (folder is not null)
            OutputFolderBox.Text = folder;
    }

    private static string? PickFolder(string description)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = description,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private void UpdateScanButton() =>
        ScanButton.IsEnabled = !string.IsNullOrWhiteSpace(SourceFolderBox.Text);

    // ── Scan ─────────────────────────────────────────────────────────

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        string folder = SourceFolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            ShowError("Folder not found", $"The folder does not exist:\n{folder}");
            return;
        }

        var cbzFiles = Directory.GetFiles(folder, "*.cbz", SearchOption.TopDirectoryOnly)
                                .Order(NaturalComparer.Instance)
                                .ToList();

        if (cbzFiles.Count == 0)
        {
            ShowError("No CBZ files", "No .cbz files were found in the selected folder.");
            return;
        }

        string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        SetScanningState(true);

        var scanProgress = new Progress<(int Current, int Total)>(p =>
        {
            ProgressBar.Value = (double)p.Current / p.Total * 100;
            StatusLabel.Text = $"Scanning {p.Current}/{p.Total}…";
        });

        _parsedFiles = await Task.Run(() =>
        {
            var results = new List<MangaFile>(cbzFiles.Count);
            for (int i = 0; i < cbzFiles.Count; i++)
            {
                var file = FileParser.Parse(cbzFiles[i], folderName);
                file.SuspectedAdEntries = AdDetector.FindSuspectedAds(cbzFiles[i]);
                results.Add(file);
                ((IProgress<(int, int)>)scanProgress).Report((i + 1, cbzFiles.Count));
            }
            return results;
        });

        SaveLastFolder(folder);
        SetScanningState(false);
        RefreshGroups();
    }

    private void SetScanningState(bool scanning)
    {
        ScanButton.IsEnabled = !scanning;
        ScanButton.Content = scanning ? "Scanning…" : "Scan";
        MergeButton.IsEnabled = !scanning && _groups.Count > 0;
        BrowseSource_IsEnabled(!scanning);
        if (scanning)
        {
            ProgressBar.Value = 0;
            StatusLabel.Text = "Scanning…";
        }
        else
        {
            ProgressBar.Value = 0;
        }
    }

    private void RefreshGroups()
    {
        int chaptersPerGroup = GetChaptersPerGroup();
        (_mode, _groups) = FileParser.Group(_parsedFiles, chaptersPerGroup);

        if (MergeAllInOneCheck.IsChecked == true && _parsedFiles.Count > 0)
        {
            string seriesName = _parsedFiles[0].SeriesName;
            _groups =
            [
                new MergeGroup
                {
                    OutputName = FileParser.SanitizeName($"{seriesName} Complete"),
                    Files = [.. _parsedFiles
                        .OrderBy(f => f.Volume ?? 0)
                        .ThenBy(f => f.Chapter ?? 0)
                        .ThenBy(f => f.FileName, NaturalComparer.Instance)]
                }
            ];
        }

        UpdateModePanel();
        BuildTree();

        MergeButton.IsEnabled = _groups.Count > 0;
        RefreshAdStatus();
    }

    private int GetChaptersPerGroup()
    {
        if (int.TryParse(ChaptersPerGroupBox.Text, out int n) && n > 0)
            return n;
        return 10;
    }

    private int GetJpegQuality()
    {
        if (int.TryParse(QualityBox.Text, out int q) && q >= 1 && q <= 100)
            return q;
        return 80;
    }

    // ── Mode Panel ───────────────────────────────────────────────────

    private void UpdateModePanel()
    {
        ModePanel.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        if (MergeAllInOneCheck.IsChecked == true)
        {
            ModeLabel.Text = "Single File";
            ModeIndicator.Background = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
            ChaptersPerGroupPanel.Visibility = Visibility.Collapsed;
        }
        else if (_mode == GroupMode.ByVolume)
        {
            ModeLabel.Text = "By Volume";
            ModeIndicator.Background = FindResource("AccentBrush") as SolidColorBrush;
            ChaptersPerGroupPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ModeLabel.Text = "By Chapter Count";
            ModeIndicator.Background = new SolidColorBrush(Color.FromRgb(0x72, 0x6B, 0xFF));
            ChaptersPerGroupPanel.Visibility = Visibility.Visible;
        }

        FileCountLabel.Text = $"{_parsedFiles.Count} file{(_parsedFiles.Count == 1 ? "" : "s")} → {_groups.Count} group{(_groups.Count == 1 ? "" : "s")}";
    }

    // ── Tree ─────────────────────────────────────────────────────────

    private void BuildTree()
    {
        GroupsTree.Items.Clear();

        foreach (var group in _groups)
        {
            var groupItem = new TreeViewItem
            {
                IsExpanded = true,
                Header = CreateGroupHeader(group)
            };

            foreach (var file in group.Files)
            {
                var fileItem = new TreeViewItem
                {
                    Header = CreateFileHeader(file),
                    Focusable = false,
                };
                fileItem.Selected += (_, _) => fileItem.IsSelected = false;
                groupItem.Items.Add(fileItem);
            }

            GroupsTree.Items.Add(groupItem);
        }
    }

    private static UIElement CreateGroupHeader(MergeGroup group)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "📦  ",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = group.OutputName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (SolidColorBrush)Application.Current.FindResource("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"  ({group.Files.Count} file{(group.Files.Count == 1 ? "" : "s")})",
            FontSize = 11,
            Foreground = (SolidColorBrush)Application.Current.FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private UIElement CreateFileHeader(MangaFile file)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = "   📄  ",
            FontSize = 12,
            Foreground = (SolidColorBrush)Application.Current.FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = file.FileName,
            FontSize = 12,
            Foreground = (SolidColorBrush)Application.Current.FindResource("TextSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (SkipAdsCheck.IsChecked == true && file.SuspectedAdEntries.Count > 0)
        {
            int n = file.SuspectedAdEntries.Count;
            panel.Children.Add(new TextBlock
            {
                Text = $"  ⚠ {n} ad{(n == 1 ? "" : "s")} skipped",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
                VerticalAlignment = VerticalAlignment.Center
            });

            foreach (var entryFullName in file.SuspectedAdEntries)
                panel.Children.Add(CreateEyeButton(file, entryFullName));
        }

        return panel;
    }

    private UIElement CreateEyeButton(MangaFile file, string entryFullName)
    {
        // Lazy-load image preview tooltip
        var previewImg = new Image { MaxHeight = 420, MaxWidth = 280, Stretch = Stretch.Uniform };
        var tooltip = new ToolTip
        {
            Background = (SolidColorBrush)Application.Current.FindResource("SurfaceBrush"),
            BorderBrush = (SolidColorBrush)Application.Current.FindResource("BorderBrush"),
            Content = new StackPanel
            {
                Margin = new Thickness(6),
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{Path.GetFileName(entryFullName)}  —  click 👁 to keep this image",
                        FontSize = 10,
                        Foreground = (SolidColorBrush)Application.Current.FindResource("TextSecondaryBrush"),
                        Margin = new Thickness(0, 0, 0, 6),
                    },
                    previewImg
                }
            }
        };

        bool imageLoaded = false;
        tooltip.Opened += async (_, _) =>
        {
            if (imageLoaded) return;
            imageLoaded = true;
            previewImg.Source = await Task.Run(() => LoadPreviewImage(file.FilePath, entryFullName));
        };

        var eye = new TextBlock
        {
            Text = " 👁",
            FontSize = 12,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
        };
        ToolTipService.SetToolTip(eye, tooltip);
        ToolTipService.SetShowDuration(eye, 12000);

        eye.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            file.SuspectedAdEntries.Remove(entryFullName);
            RefreshAdStatus();
            BuildTree();
        };

        return eye;
    }

    private static BitmapImage? LoadPreviewImage(string cbzPath, string entryFullName)
    {
        try
        {
            using var zip = ZipFile.OpenRead(cbzPath);
            var entry = zip.GetEntry(entryFullName);
            if (entry is null) return null;

            var ms = new MemoryStream();
            using (var s = entry.Open())
                s.CopyTo(ms);
            ms.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelHeight = 420;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            ms.Dispose();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ── Ad status ────────────────────────────────────────────────────

    private void RefreshAdStatus()
    {
        int total = _parsedFiles.Sum(f => f.SuspectedAdEntries.Count);
        bool active = total > 0 && SkipAdsCheck.IsChecked == true;
        string adNote = active
            ? $" — ⚠ {total} suspected ad image{(total == 1 ? "" : "s")} will be skipped"
            : "";
        StatusLabel.Text = $"Ready — {_groups.Count} group{(_groups.Count == 1 ? "" : "s")} to merge{adNote}";
    }

    // ── Chapter count changed → re-group ────────────────────────────

    private void ChaptersPerGroup_Changed(object sender, TextChangedEventArgs e)
    {
        if (_parsedFiles.Count > 0 && _mode == GroupMode.ByChapterCount && MergeAllInOneCheck.IsChecked != true)
            RefreshGroups();
    }

    private void MergeAllInOne_Changed(object sender, RoutedEventArgs e)
    {
        if (_parsedFiles.Count > 0) RefreshGroups();
    }

    private void SkipAds_Changed(object sender, RoutedEventArgs e)
    {
        if (_parsedFiles.Count > 0)
        {
            RefreshAdStatus();
            BuildTree();
        }
    }

    private void CompressImages_Changed(object sender, RoutedEventArgs e) =>
        CompressQualityPanel.Visibility = CompressImagesCheck.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;

    // ── Number-only input ────────────────────────────────────────────

    private void NumberOnly_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);

    // ── Merge ────────────────────────────────────────────────────────

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        string outputFolder = string.IsNullOrWhiteSpace(OutputFolderBox.Text)
            ? Path.Combine(SourceFolderBox.Text.Trim(), "Merged")
            : OutputFolderBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            ShowError("No output folder", "Please select an output folder.");
            return;
        }

        // Warn if output is same as source
        string source = Path.GetFullPath(SourceFolderBox.Text.Trim());
        string output = Path.GetFullPath(outputFolder);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase))
        {
            var confirm = MessageBox.Show(
                "Output folder is the same as the source folder.\nThis may overwrite original files if names clash.\n\nContinue?",
                "Warning", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
        }

        SetMergingState(true);

        _cts = new CancellationTokenSource();
        var progress = new Progress<(int completed, int total, string status)>(r =>
        {
            ProgressBar.Value = r.total > 0 ? (double)r.completed / r.total * 100 : 0;
            StatusLabel.Text = r.status;
        });

        try
        {
            bool skipAds = SkipAdsCheck.IsChecked == true;
            int? jpegQuality = CompressImagesCheck.IsChecked == true ? GetJpegQuality() : null;
            await CbzMerger.MergeAllAsync(_groups, outputFolder, skipAds, jpegQuality, progress, _cts.Token);
            ProgressBar.Value = 100;
            StatusLabel.Text = $"Done! {_groups.Count} file{(_groups.Count == 1 ? "" : "s")} created in: {outputFolder}";

            var open = MessageBox.Show(
                $"Successfully merged {_groups.Count} group{(_groups.Count == 1 ? "" : "s")}.\n\nOpen output folder?",
                "Merge Complete", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start("explorer.exe", outputFolder);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Cancelled.";
            ProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            ShowError("Merge failed", ex.Message);
            StatusLabel.Text = "Error — see details above.";
        }
        finally
        {
            SetMergingState(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) =>
        _cts?.Cancel();

    private void SetMergingState(bool merging)
    {
        MergeButton.IsEnabled = !merging;
        MergeButton.Content = merging ? "Merging..." : "Merge All";
        CancelButton.Visibility = merging ? Visibility.Visible : Visibility.Collapsed;
        ScanButton.IsEnabled = !merging;
        BrowseSource_IsEnabled(!merging);
    }

    private void BrowseSource_IsEnabled(bool enabled)
    {
        SourceFolderBox.IsReadOnly = !enabled;
        OutputFolderBox.IsReadOnly = !enabled;
    }

    // ── Persistence ──────────────────────────────────────────────────

    private void LoadLastFolder()
    {
        try
        {
            if (File.Exists(LastFolderFile))
                SourceFolderBox.Text = File.ReadAllText(LastFolderFile).Trim();
        }
        catch { }
    }

    private static void SaveLastFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastFolderFile)!);
            File.WriteAllText(LastFolderFile, folder);
        }
        catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
