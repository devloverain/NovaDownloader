using System.Diagnostics;
using System.IO;
using System.Windows;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Utils;

namespace NovaDownloader.UI.Dialogs;

public partial class DownloadCompletedDialog : Window
{
    private readonly DownloadTask _task;

    public DownloadCompletedDialog(DownloadTask task)
    {
        InitializeComponent();
        _task = task;
        DataContext = _task;

        var sizeText = _task.TotalBytes > 0 ? FormatUtils.FormatBytes(_task.TotalBytes) : FormatUtils.FormatBytes(_task.DownloadedBytes);
        var duration = _task.CompletedAt.HasValue ? _task.CompletedAt.Value - _task.CreatedAt : (TimeSpan?)null;
        SummaryText.Text = duration is { TotalSeconds: >= 1 }
            ? $"{sizeText} • Saved in {FormatUtils.FormatTimeSpan(duration)}"
            : $"{sizeText} • Saved";

        var exists = !string.IsNullOrWhiteSpace(_task.DestinationPath) && File.Exists(_task.DestinationPath);
        OpenFileBtn.IsEnabled = exists;
        OpenWithBtn.IsEnabled = exists;
        OpenFolderBtn.IsEnabled = !string.IsNullOrWhiteSpace(_task.DestinationPath);
    }

    private void OpenFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_task.DestinationPath) || !File.Exists(_task.DestinationPath))
        {
            MessageBox.Show("The file was moved or deleted from its original path.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_task.DestinationPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenWithButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_task.DestinationPath) || !File.Exists(_task.DestinationPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{_task.DestinationPath}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open 'Open With' dialog: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_task.DestinationPath) && File.Exists(_task.DestinationPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_task.DestinationPath}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var folder = Path.GetDirectoryName(_task.DestinationPath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("The target folder no longer exists.", "Folder Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
