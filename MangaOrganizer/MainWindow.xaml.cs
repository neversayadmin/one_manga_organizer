using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MangaOrganizer.Models;
using MangaOrganizer.Services;

namespace MangaOrganizer;

public partial class MainWindow : Window
{
    private List<MangaFile> _parsedFiles = [];
    private List<MergeGroup> _groups = [];
    private GroupMode _mode = GroupMode.ByVolume;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        SourceFolderBox.TextChanged += (_, _) => UpdateScanButton();
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

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        string folder = SourceFolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            ShowError("Folder not found", $"The folder does not exist:\n{folder}");
            return;
        }

        var cbzFiles = Directory.GetFiles(folder, "*.cbz", SearchOption.TopDirectoryOnly)
                                .OrderBy(f => f)
                                .ToList();

        if (cbzFiles.Count == 0)
        {
            ShowError("No CBZ files", "No .cbz files were found in the selected folder.");
            return;
        }

        string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _parsedFiles = cbzFiles.Select(f => FileParser.Parse(f, folderName)).ToList();

        RefreshGroups();
    }

    private void RefreshGroups()
    {
        int chaptersPerGroup = GetChaptersPerGroup();
        (_mode, _groups) = FileParser.Group(_parsedFiles, chaptersPerGroup);

        UpdateModePanel();
        BuildTree();

        MergeButton.IsEnabled = _groups.Count > 0;
        StatusLabel.Text = $"Ready — {_groups.Count} group{(_groups.Count == 1 ? "" : "s")} to merge";
    }

    private int GetChaptersPerGroup()
    {
        if (int.TryParse(ChaptersPerGroupBox.Text, out int n) && n > 0)
            return n;
        return 10;
    }

    // ── Mode Panel ───────────────────────────────────────────────────

    private void UpdateModePanel()
    {
        ModePanel.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        if (_mode == GroupMode.ByVolume)
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
                groupItem.Items.Add(new TreeViewItem
                {
                    Header = CreateFileHeader(file),
                    IsEnabled = false
                });
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

    private static UIElement CreateFileHeader(MangaFile file)
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
        return panel;
    }

    // ── Chapter count changed → re-group ────────────────────────────

    private void ChaptersPerGroup_Changed(object sender, TextChangedEventArgs e)
    {
        if (_parsedFiles.Count > 0 && _mode == GroupMode.ByChapterCount)
            RefreshGroups();
    }

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
        var progress = new Progress<(int g, int gt, int p, int pt, string status)>(r =>
        {
            double groupFraction = (double)(r.g - 1) / r.gt;
            double pageFraction = r.pt > 0 ? (double)r.p / r.pt / r.gt : 0;
            ProgressBar.Value = (groupFraction + pageFraction) * 100;
            StatusLabel.Text = $"Group {r.g}/{r.gt} — {r.status}";
        });

        try
        {
            await CbzMerger.MergeAllAsync(_groups, outputFolder, progress, _cts.Token);
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

    // ── Helpers ──────────────────────────────────────────────────────

    private void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
