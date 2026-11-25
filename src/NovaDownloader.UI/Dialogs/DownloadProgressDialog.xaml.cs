using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using NovaDownloader.Core.Models;

namespace NovaDownloader.UI.Dialogs;

public partial class DownloadProgressDialog : Window
{
    private readonly DownloadTask _task;
    private readonly Func<DownloadTask, Task> _onPause;
    private readonly Func<DownloadTask, Task> _onResume;
    private readonly Func<DownloadTask, Task>? _onCancel;
    private bool _completionHandled;

    public Guid TaskId => _task.Id;

    public DownloadProgressDialog(
        DownloadTask task,
        Func<DownloadTask, Task> onPause,
        Func<DownloadTask, Task> onResume,
        Func<DownloadTask, Task>? onCancel = null)
    {
        InitializeComponent();
        _task = task;
        _onPause = onPause;
        _onResume = onResume;
        _onCancel = onCancel;

        DataContext = _task;
        _task.PropertyChanged += Task_PropertyChanged;
        UpdateUiState();
    }

    private void Task_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadTask.Status)
            or nameof(DownloadTask.ProgressPercentage)
            or nameof(DownloadTask.FileName))
        {
            Dispatcher.BeginInvoke(UpdateUiState);
        }
    }

    private void UpdateUiState()
    {
        HeaderPercentText.Text = $"{_task.ProgressPercentage:F1}%";
        HeaderTitleText.Text = _task.FileName;

        var isCompleted = _task.Status == DownloadStatus.Completed;
        var isPaused = _task.Status == DownloadStatus.Paused;
        var isFailed = _task.Status == DownloadStatus.Failed;
        var isCancelled = _task.Status == DownloadStatus.Cancelled;

        if (isCompleted && !_completionHandled)
        {
            _completionHandled = true;
            Close();
            return;
        }

        if (isCancelled)
        {
            Close();
            return;
        }

        PauseResumeBtn.IsEnabled = !isCompleted;
        PauseResumeBtn.Content = (isPaused || isFailed) ? "Resume" : "Pause";
        OpenFolderBtn.IsEnabled = !string.IsNullOrWhiteSpace(_task.DestinationPath);
    }

    private async void PauseResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_task.Status == DownloadStatus.Paused || _task.Status == DownloadStatus.Failed)
        {
            await _onResume(_task);
        }
        else if (_task.Status == DownloadStatus.Downloading)
        {
            await _onPause(_task);
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onCancel != null)
        {
            await _onCancel(_task);
        }
        else
        {
            await _onPause(_task);
        }
        Close();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(_task.DestinationPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_task.DestinationPath}\"")
                {
                    UseShellExecute = true
                });
            }
            else
            {
                var folder = Path.GetDirectoryName(_task.DestinationPath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
                }
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

    protected override void OnClosed(EventArgs e)
    {
        _task.PropertyChanged -= Task_PropertyChanged;
        base.OnClosed(e);
    }
}
