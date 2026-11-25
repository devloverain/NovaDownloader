using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NovaDownloader.Core.Utils;
using NovaDownloader.UI.Models;
using NovaDownloader.UI.Services;

namespace NovaDownloader.UI.Dialogs;

public partial class DownloadFileInfoDialog : Window
{
    private readonly QueueSchedulerService? _schedulerService;
    private bool _isInitializing = true;
    private bool _userEditedPath;
    private string _currentCategoryFolder = string.Empty;

    public string DownloadUrl => UrlTextBox.Text.Trim();
    public string DestinationPath => SaveAsTextBox.Text.Trim();
    public string SelectedCategory => CategoryComboBox.SelectedItem?.ToString() ?? "General";
    public string SelectedQueueId => (QueueComboBox.SelectedItem as ScheduleQueue)?.Id ?? ScheduleQueue.MainQueueId;
    public string DescriptionText => DescriptionTextBox.Text.Trim();
    public bool DownloadLater { get; private set; }

    public DownloadFileInfoDialog(
        string? initialUrl = null,
        string? suggestedFileName = null,
        string? description = null,
        long? fileSize = null,
        string? mimeType = null,
        string? quality = null,
        QueueSchedulerService? schedulerService = null,
        string? preferredQueueId = null)
    {
        InitializeComponent();
        _schedulerService = schedulerService;

        CategoryComboBox.ItemsSource = UserSettingsService.Categories;
        PopulateQueues(preferredQueueId);

        var url = (initialUrl ?? string.Empty).Trim();
        var isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (isHttp)
        {
            UrlTextBox.Text = url;
        }

        var detectedCategory = UserSettingsService.DetectCategory(suggestedFileName, url, mimeType);
        CategoryComboBox.SelectedItem = detectedCategory;

        RememberPathCheckBox.IsChecked = UserSettingsService.Current.RememberCategoryPaths;
        _currentCategoryFolder = UserSettingsService.GetPathForCategory(detectedCategory);
        RememberPathLabel.Text = $"Remember save folder for \"{detectedCategory}\"";

        var fileName = ResolveFileName(url, suggestedFileName, quality);
        SaveAsTextBox.Text = Path.Combine(_currentCategoryFolder, fileName);

        if (!string.IsNullOrWhiteSpace(description))
        {
            DescriptionTextBox.Text = description;
        }
        else if (!string.IsNullOrWhiteSpace(quality) && YtDlpService.IsSupportedVideoPage(url))
        {
            DescriptionTextBox.Text = $"YouTube ({quality})";
        }

        UpdatePreview(fileName, fileSize, quality);
        _isInitializing = false;
    }

    private void PopulateQueues(string? selectQueueId = null)
    {
        var queues = _schedulerService?.Queues.ToList()
            ?? [new ScheduleQueue { Id = ScheduleQueue.MainQueueId, Name = "Main Queue" }];

        QueueComboBox.ItemsSource = queues;
        var match = !string.IsNullOrWhiteSpace(selectQueueId)
            ? queues.FirstOrDefault(q => string.Equals(q.Id, selectQueueId, StringComparison.OrdinalIgnoreCase))
            : null;
        QueueComboBox.SelectedItem = match ?? queues.First();
    }

    private static string ResolveFileName(string url, string? suggestedFileName, string? quality)
    {
        var name = suggestedFileName?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            if (!string.IsNullOrWhiteSpace(quality)
                && (quality.Contains("mp3", StringComparison.OrdinalIgnoreCase) || quality.Contains("audio", StringComparison.OrdinalIgnoreCase))
                && !name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.ChangeExtension(name, ".mp3");
            }
            return name;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var localName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(localName) && localName.Contains('.'))
                return localName;
        }

        if (YtDlpService.IsSupportedVideoPage(url))
        {
            var ext = quality?.Contains("mp3", StringComparison.OrdinalIgnoreCase) == true ? ".mp3" : ".mp4";
            return "video_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ext;
        }

        return "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
    }

    private void UpdatePreview(string fileName, long? fileSize = null, string? quality = null)
    {
        PreviewFileNameText.Text = string.IsNullOrWhiteSpace(fileName) ? "New Download" : fileName;

        var ext = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(ext)) ext = "FILE";
        ExtBadgeText.Text = ext.Length > 5 ? ext[..5] : ext;

        var iconName = ext.ToLowerInvariant() switch
        {
            "iso" or "img" or "vhd" => "file_iso",
            "mp4" or "mkv" or "webm" or "ts" or "m3u8" or "avi" or "mov" => "file_video",
            "zip" or "rar" or "7z" or "tar" or "gz" => "file_archive",
            "exe" or "msi" or "bat" or "cmd" => "file_exe",
            "mp3" or "aac" or "flac" or "m4a" or "wav" or "ogg" => "file_audio",
            _ => "file_pdf"
        };

        try
        {
            var img = new BitmapImage(new Uri($"pack://application:,,,/Assets/Icons/{iconName}.png", UriKind.Absolute));
            img.Freeze();
            PreviewIcon.Source = img;
        }
        catch { }

        var parts = new List<string>();
        if (fileSize is > 0)
            parts.Add(FormatUtils.FormatBytes(fileSize.Value));
        if (!string.IsNullOrWhiteSpace(quality))
            parts.Add(quality);

        MetaInfoText.Text = parts.Count > 0 ? string.Join(" • ", parts) : "Ready to download";
    }

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || _userEditedPath) return;

        var url = UrlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) return;

        var category = UserSettingsService.DetectCategory(null, url);
        _isInitializing = true;
        CategoryComboBox.SelectedItem = category;
        _currentCategoryFolder = UserSettingsService.GetPathForCategory(category);
        RememberPathLabel.Text = $"Remember save folder for \"{category}\"";
        var fileName = ResolveFileName(url, null, null);
        SaveAsTextBox.Text = Path.Combine(_currentCategoryFolder, fileName);
        UpdatePreview(fileName);
        _isInitializing = false;
    }

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var category = SelectedCategory;
        _currentCategoryFolder = UserSettingsService.GetPathForCategory(category);
        RememberPathLabel.Text = $"Remember save folder for \"{category}\"";

        var currentFile = Path.GetFileName(SaveAsTextBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(currentFile))
            currentFile = "download_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

        SaveAsTextBox.Text = Path.Combine(_currentCategoryFolder, currentFile);
    }

    private void SaveAsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        _userEditedPath = true;
        var name = Path.GetFileName(SaveAsTextBox.Text.Trim());
        if (!string.IsNullOrWhiteSpace(name))
            UpdatePreview(name);
    }

    private void RememberPathCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UserSettingsService.Current.RememberCategoryPaths = RememberPathCheckBox.IsChecked == true;
        UserSettingsService.Save();
    }

    private void ChangeCategoryFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = $"Select folder for {SelectedCategory}",
            InitialDirectory = Directory.Exists(_currentCategoryFolder)
                ? _currentCategoryFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dlg.ShowDialog() == true)
        {
            _currentCategoryFolder = dlg.FolderName;
            var currentFile = Path.GetFileName(SaveAsTextBox.Text.Trim());
            if (!string.IsNullOrWhiteSpace(currentFile))
            {
                SaveAsTextBox.Text = Path.Combine(dlg.FolderName, currentFile);
            }
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = SaveAsTextBox.Text.Trim();
        var initialDir = Path.GetDirectoryName(currentPath);
        var initialFile = Path.GetFileName(currentPath);

        var dlg = new SaveFileDialog
        {
            InitialDirectory = Directory.Exists(initialDir) ? initialDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FileName = initialFile
        };

        if (dlg.ShowDialog() == true)
        {
            SaveAsTextBox.Text = dlg.FileName;
            var chosenDir = Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrWhiteSpace(chosenDir))
                _currentCategoryFolder = chosenDir;
        }
    }

    private void ToggleNewSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerService == null) return;
        QuickNewSchedulePanel.Visibility = QuickNewSchedulePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (QuickNewSchedulePanel.Visibility == Visibility.Visible)
        {
            NewScheduleNameBox.Text = string.Empty;
            NewScheduleNameBox.Focus();
        }
    }

    private void NewScheduleNameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            CreateNewSchedule_Click(sender, e);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CancelNewSchedule_Click(sender, e);
        }
    }

    private void CreateNewSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerService == null) return;
        var name = NewScheduleNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Enter a schedule name (e.g., Games).", "Schedule Name", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var created = _schedulerService.CreateQueue(name);
        PopulateQueues(created.Id);
        QuickNewSchedulePanel.Visibility = Visibility.Collapsed;
    }

    private void CancelNewSchedule_Click(object sender, RoutedEventArgs e)
    {
        QuickNewSchedulePanel.Visibility = Visibility.Collapsed;
    }

    private void DownloadLaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        SaveCategoryPathIfChecked();
        DownloadLater = true;
        DialogResult = true;
        Close();
    }

    private void StartDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        SaveCategoryPathIfChecked();
        DownloadLater = false;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private bool ValidateInputs()
    {
        if (string.IsNullOrWhiteSpace(DownloadUrl))
        {
            MessageBox.Show("Please enter a valid download URL.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            MessageBox.Show("Please specify a destination file path.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return true;
    }

    private void SaveCategoryPathIfChecked()
    {
        UserSettingsService.Current.RememberCategoryPaths = RememberPathCheckBox.IsChecked == true;
        var folderToSave = Path.GetDirectoryName(DestinationPath) ?? _currentCategoryFolder;
        if (RememberPathCheckBox.IsChecked == true && !string.IsNullOrWhiteSpace(folderToSave))
        {
            UserSettingsService.SetPathForCategory(SelectedCategory, folderToSave);
        }
        else
        {
            UserSettingsService.Save();
        }
    }
}
