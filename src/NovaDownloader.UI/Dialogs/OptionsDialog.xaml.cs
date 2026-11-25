using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NovaDownloader.UI.Services;

namespace NovaDownloader.UI.Dialogs;

public partial class OptionsDialog : Window
{
    private readonly Dictionary<string, string> _pendingCategoryPaths;
    private readonly Dictionary<string, string> _pendingCategoryExtensions;
    private bool _isLoadingCategory;

    public int MaxConcurrentDownloads { get; private set; }

    public OptionsDialog(int currentMaxConcurrent)
    {
        InitializeComponent();
        MaxConcurrentDownloads = currentMaxConcurrent;

        var settings = UserSettingsService.Current;
        _pendingCategoryPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _pendingCategoryExtensions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cat in UserSettingsService.Categories)
        {
            _pendingCategoryPaths[cat] = UserSettingsService.GetPathForCategory(cat);
            _pendingCategoryExtensions[cat] = UserSettingsService.GetExtensionsForCategory(cat);
        }

        // General & Startup
        var isRegistered = StartupService.IsRegistered();
        LaunchOnStartupCheckBox.IsChecked = isRegistered || settings.LaunchOnStartup;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedOnStartup;
        StartMinimizedCheckBox.IsEnabled = LaunchOnStartupCheckBox.IsChecked == true;
        ShowCompletedDialogCheckBox.IsChecked = settings.ShowCompletedDialog;
        PlaySoundCheckBox.IsChecked = settings.PlaySoundOnComplete;
        AutoExtractCheckBox.IsChecked = settings.AutoExtractArchives;

        DuplicateBehaviorCombo.ItemsSource = new[]
        {
            "Auto-rename (e.g. file (1).zip)",
            "Overwrite existing file",
            "Ask / Auto-rename"
        };
        DuplicateBehaviorCombo.SelectedIndex = settings.DuplicateBehavior switch
        {
            DuplicateFileBehavior.Overwrite => 1,
            DuplicateFileBehavior.Ask => 2,
            _ => 0
        };

        // Folders & Types
        DefaultFolderBox.Text = settings.DefaultDownloadFolder;
        CategorySelectCombo.ItemsSource = UserSettingsService.Categories;
        CategorySelectCombo.SelectedIndex = 0;

        // Speed & Network
        MaxConcurrentBox.Text = currentMaxConcurrent.ToString();
        SegmentsSlider.Value = Math.Clamp(settings.DefaultSegments, 1, 16);
        SegmentsValueText.Text = $"{(int)SegmentsSlider.Value} threads";

        SpeedLimitCheckBox.IsChecked = settings.SpeedLimitEnabled;
        SpeedLimitBox.Text = (settings.SpeedLimitKBps > 0 ? settings.SpeedLimitKBps : 2048).ToString();
        SpeedLimitBox.IsEnabled = settings.SpeedLimitEnabled;

        ProxyEnabledCheckBox.IsChecked = settings.ProxyEnabled;
        ProxyAddressBox.Text = settings.ProxyAddress;
        ProxyUserBox.Text = settings.ProxyUsername;
        ProxyPassBox.Text = settings.ProxyPassword;
        UpdateProxyInputsEnabled();
        UserAgentBox.Text = settings.CustomUserAgent;

        // Tools
        YtDlpPathBox.Text = settings.CustomYtDlpPath;
        FfmpegPathBox.Text = settings.CustomFfmpegPath;
        RefreshToolStatuses();
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelGeneral == null) return;
        PanelGeneral.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelFolders.Visibility = TabFolders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = TabNetwork.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelTools.Visibility = TabTools.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CategorySelectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategorySelectCombo.SelectedItem is not string cat) return;
        _isLoadingCategory = true;
        CategoryPathBox.Text = _pendingCategoryPaths.TryGetValue(cat, out var p)
            ? p
            : UserSettingsService.GetPathForCategory(cat);
        CategoryExtensionsBox.Text = _pendingCategoryExtensions.TryGetValue(cat, out var ex)
            ? ex
            : UserSettingsService.GetExtensionsForCategory(cat);
        CategoryExtensionsBox.IsEnabled = !string.Equals(cat, "General", StringComparison.OrdinalIgnoreCase);
        _isLoadingCategory = false;
    }

    private void CategoryPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingCategory) return;
        if (CategorySelectCombo.SelectedItem is string cat)
        {
            _pendingCategoryPaths[cat] = CategoryPathBox.Text.Trim();
        }
    }

    private void CategoryExtensionsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingCategory) return;
        if (CategorySelectCombo.SelectedItem is string cat)
        {
            _pendingCategoryExtensions[cat] = CategoryExtensionsBox.Text.Trim();
        }
    }

    private void BrowseRootFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Default Download Folder",
            InitialDirectory = Directory.Exists(DefaultFolderBox.Text)
                ? DefaultFolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dlg.ShowDialog() == true)
        {
            DefaultFolderBox.Text = dlg.FolderName;
        }
    }

    private void ApplyRootToCategories_Click(object sender, RoutedEventArgs e)
    {
        var root = DefaultFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root)) return;

        foreach (var cat in UserSettingsService.Categories)
        {
            _pendingCategoryPaths[cat] = UserSettingsService.GetDefaultPathForCategory(cat, root);
        }

        if (CategorySelectCombo.SelectedItem is string selectedCat)
        {
            _isLoadingCategory = true;
            CategoryPathBox.Text = _pendingCategoryPaths[selectedCat];
            _isLoadingCategory = false;
        }
    }

    private void ResetCategoryDefaults_Click(object sender, RoutedEventArgs e)
    {
        var root = DefaultFolderBox.Text.Trim();
        foreach (var cat in UserSettingsService.Categories)
        {
            _pendingCategoryPaths[cat] = UserSettingsService.GetDefaultPathForCategory(cat, root);
            _pendingCategoryExtensions[cat] = UserSettingsService.GetDefaultExtensionsForCategory(cat);
        }

        CategorySelectCombo_SelectionChanged(CategorySelectCombo, null!);
    }

    private void BrowseCategoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CategorySelectCombo.SelectedItem is not string cat) return;

        var current = CategoryPathBox.Text.Trim();
        var dlg = new OpenFolderDialog
        {
            Title = $"Select folder for {cat}",
            InitialDirectory = Directory.Exists(current)
                ? current
                : (Directory.Exists(DefaultFolderBox.Text) ? DefaultFolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        };

        if (dlg.ShowDialog() == true)
        {
            CategoryPathBox.Text = dlg.FolderName;
            _pendingCategoryPaths[cat] = dlg.FolderName;
        }
    }

    private void LaunchOnStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (StartMinimizedCheckBox != null)
            StartMinimizedCheckBox.IsEnabled = LaunchOnStartupCheckBox.IsChecked == true;
    }

    private void SegmentsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SegmentsValueText != null)
            SegmentsValueText.Text = $"{(int)SegmentsSlider.Value} threads";
    }

    private void SpeedLimitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (SpeedLimitBox != null)
            SpeedLimitBox.IsEnabled = SpeedLimitCheckBox.IsChecked == true;
    }

    private void SpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string val)
        {
            SpeedLimitCheckBox.IsChecked = true;
            SpeedLimitBox.Text = val;
        }
    }

    private void ProxyCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateProxyInputsEnabled();

    private void UpdateProxyInputsEnabled()
    {
        if (ProxyAddressBox == null) return;
        var enabled = ProxyEnabledCheckBox.IsChecked == true;
        ProxyAddressBox.IsEnabled = enabled;
        ProxyUserBox.IsEnabled = enabled;
        ProxyPassBox.IsEnabled = enabled;
    }

    private void OpenBrowserIntegration_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new BrowserIntegrationDialog { Owner = this };
        dlg.ShowDialog();
    }

    private void RefreshToolStatuses()
    {
        var green = (Brush)new BrushConverter().ConvertFromString("#4ADE80")!;
        var amber = (Brush)new BrushConverter().ConvertFromString("#FBBF24")!;

        var ytPath = !string.IsNullOrWhiteSpace(YtDlpPathBox.Text) && File.Exists(YtDlpPathBox.Text)
            ? YtDlpPathBox.Text
            : YtDlpService.FindYtDlpExe();

        YtDlpStatusText.Text = ytPath != null ? "Found" : "Missing";
        YtDlpStatusText.Foreground = ytPath != null ? green : amber;
        YtDlpStatusText.ToolTip = ytPath ?? "Not found in Tools\\ or PATH";

        var ffPath = !string.IsNullOrWhiteSpace(FfmpegPathBox.Text) && File.Exists(FfmpegPathBox.Text)
            ? FfmpegPathBox.Text
            : YtDlpService.FindFfmpegExe();

        FfmpegStatusText.Text = ffPath != null ? "Found" : "Missing";
        FfmpegStatusText.Foreground = ffPath != null ? green : amber;
        FfmpegStatusText.ToolTip = ffPath ?? "Not found in Tools\\ or PATH";
    }

    private void BrowseYtDlp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select yt-dlp.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            YtDlpPathBox.Text = dlg.FileName;
            RefreshToolStatuses();
        }
    }

    private void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select ffmpeg.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            FfmpegPathBox.Text = dlg.FileName;
            RefreshToolStatuses();
        }
    }

    private async void UpdateYtDlp_Click(object sender, RoutedEventArgs e)
    {
        var exe = !string.IsNullOrWhiteSpace(YtDlpPathBox.Text) && File.Exists(YtDlpPathBox.Text)
            ? YtDlpPathBox.Text
            : YtDlpService.FindYtDlpExe();

        if (exe == null)
        {
            ToolsFeedbackText.Text = "yt-dlp.exe was not found on PATH or in Tools\\.";
            return;
        }

        UpdateYtDlpBtn.IsEnabled = false;
        ToolsFeedbackText.Text = "Updating yt-dlp...";
        try
        {
            var output = await Task.Run(() =>
            {
                using var proc = Process.Start(new ProcessStartInfo(exe, "-U")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc == null) return "Could not start yt-dlp.";
                var text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit(30000);
                return text.Trim();
            });

            ToolsFeedbackText.Text = string.IsNullOrWhiteSpace(output)
                ? "yt-dlp update check finished."
                : output.Split('\n').LastOrDefault()?.Trim() ?? "yt-dlp updated.";
        }
        catch (Exception ex)
        {
            ToolsFeedbackText.Text = $"Update failed: {ex.Message}";
        }
        finally
        {
            UpdateYtDlpBtn.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxConcurrentBox.Text.Trim(), out var maxConc) || maxConc < 1 || maxConc > 16)
        {
            MessageBox.Show("Please enter Max Concurrent Downloads between 1 and 16.", "Invalid Value", MessageBoxButton.OK, MessageBoxImage.Warning);
            TabNetwork.IsChecked = true;
            return;
        }

        var speedKbps = 2048;
        if (SpeedLimitCheckBox.IsChecked == true && (!int.TryParse(SpeedLimitBox.Text.Trim(), out speedKbps) || speedKbps < 10))
        {
            MessageBox.Show("Please enter a valid speed limit in KB/s (minimum 10 KB/s).", "Invalid Speed Limit", MessageBoxButton.OK, MessageBoxImage.Warning);
            TabNetwork.IsChecked = true;
            return;
        }

        var rootFolder = DefaultFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rootFolder))
        {
            MessageBox.Show("Please specify a valid Default Download Folder.", "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            TabFolders.IsChecked = true;
            return;
        }

        MaxConcurrentDownloads = maxConc;

        var s = UserSettingsService.Current;
        s.MaxConcurrentDownloads = maxConc;
        s.DefaultSegments = (int)SegmentsSlider.Value;
        s.DefaultDownloadFolder = rootFolder;
        s.LaunchOnStartup = LaunchOnStartupCheckBox.IsChecked == true;
        s.StartMinimizedOnStartup = StartMinimizedCheckBox.IsChecked == true;
        s.ShowCompletedDialog = ShowCompletedDialogCheckBox.IsChecked == true;
        s.PlaySoundOnComplete = PlaySoundCheckBox.IsChecked == true;
        s.AutoExtractArchives = AutoExtractCheckBox.IsChecked == true;
        s.DuplicateBehavior = DuplicateBehaviorCombo.SelectedIndex switch
        {
            1 => DuplicateFileBehavior.Overwrite,
            2 => DuplicateFileBehavior.Ask,
            _ => DuplicateFileBehavior.AutoRename
        };

        s.SpeedLimitEnabled = SpeedLimitCheckBox.IsChecked == true;
        s.SpeedLimitKBps = speedKbps;
        s.ProxyEnabled = ProxyEnabledCheckBox.IsChecked == true;
        s.ProxyAddress = ProxyAddressBox.Text.Trim();
        s.ProxyUsername = ProxyUserBox.Text.Trim();
        s.ProxyPassword = ProxyPassBox.Text;
        s.CustomUserAgent = string.IsNullOrWhiteSpace(UserAgentBox.Text) ? s.CustomUserAgent : UserAgentBox.Text.Trim();
        s.CustomYtDlpPath = YtDlpPathBox.Text.Trim();
        s.CustomFfmpegPath = FfmpegPathBox.Text.Trim();

        foreach (var kvp in _pendingCategoryPaths)
        {
            if (!string.IsNullOrWhiteSpace(kvp.Value))
                s.CategoryPaths[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in _pendingCategoryExtensions)
        {
            s.CategoryExtensions[kvp.Key] = kvp.Value;
        }

        UserSettingsService.Save();
        StartupService.SetEnabled(s.LaunchOnStartup, s.StartMinimizedOnStartup);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
