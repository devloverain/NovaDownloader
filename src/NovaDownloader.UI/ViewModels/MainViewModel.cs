using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Queue;
using NovaDownloader.Core.Utils;
using NovaDownloader.Media;
using NovaDownloader.Media.Models;
using NovaDownloader.UI.Common;
using NovaDownloader.UI.Dialogs;
using NovaDownloader.UI.Models;
using NovaDownloader.UI.Services;

namespace NovaDownloader.UI.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IDownloadQueueManager _queueManager;
    private readonly IMediaStreamPipeline _mediaPipeline;
    private readonly IpcService _ipcService;
    private readonly QueueSchedulerService _schedulerService;
    private readonly DispatcherTimer _speedTimer;
    private readonly List<DownloadTask> _selectedTasks = [];
    private readonly HashSet<Guid> _trackedTaskIds = [];
    private readonly HashSet<Guid> _completionDialogShownIds = [];
    private readonly HashSet<Guid> _scheduledTaskIds = [];
    private readonly Dictionary<Guid, DownloadProgressDialog> _openProgressDialogs = [];
    private readonly Dictionary<Guid, System.Windows.Forms.Screen> _taskTargetScreens = [];

    private string _searchQuery = string.Empty;
    private string _selectedCategory = "All";
    private string _statusText = "Ready • Listening for Browser Downloads";
    private double _totalSpeedBytesPerSec;
    private DownloadTask? _selectedTask;

    public ObservableCollection<DownloadTask> FilteredTasks { get; } = new();
    public QueueSchedulerService SchedulerService => _schedulerService;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                ApplyFilter();
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
                ApplyFilter();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double TotalSpeedBytesPerSec
    {
        get => _totalSpeedBytesPerSec;
        set
        {
            if (SetProperty(ref _totalSpeedBytesPerSec, value))
                OnPropertyChanged(nameof(FormattedTotalSpeed));
        }
    }

    public string FormattedTotalSpeed => FormatUtils.FormatSpeed(TotalSpeedBytesPerSec);

    public int ActiveCount => _queueManager.Tasks.Count(t => t.Status == DownloadStatus.Downloading);
    public int QueuedCount => _queueManager.Tasks.Count(t => t.Status == DownloadStatus.Pending);
    public int CompletedCount => _queueManager.Tasks.Count(t => t.Status == DownloadStatus.Completed);
    public int TotalCount => _queueManager.Tasks.Count;
    public bool IsQueueRunning => _queueManager.IsRunning;

    public DownloadTask? SelectedTask
    {
        get => _selectedTask;
        set => SetProperty(ref _selectedTask, value);
    }

    // Commands
    public ICommand AddDownloadCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand DeleteCompletedCommand { get; }
    public ICommand PauseAllCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand ResumeAllCommand { get; }
    public ICommand StartQueueCommand { get; }
    public ICommand StopQueueCommand { get; }
    public ICommand OptionsCommand { get; }
    public ICommand SchedulerCommand { get; }
    public ICommand CopyUrlCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand InstallExtensionCommand { get; }
    public ICommand SelectCategoryCommand { get; }

    public MainViewModel(IDownloadQueueManager queueManager, IMediaStreamPipeline mediaPipeline, IpcService ipcService)
    {
        _queueManager = queueManager;
        _mediaPipeline = mediaPipeline;
        _ipcService = ipcService;
        _schedulerService = new QueueSchedulerService(_queueManager, StartExternalOrMediaTaskAsync, msg => StatusText = msg);
        _schedulerService.ScheduledTaskStarted += (_, task) =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _scheduledTaskIds.Add(task.Id);
                OpenProgressDialog(task, startMinimized: true);
            });
        };

        _queueManager.TaskAdded += (s, task) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                TrackTaskCompletion(task);
                ApplyFilter();
                UpdateCounts();
            });
        };

        _queueManager.TaskStatusChanged += (s, task) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ApplyFilter();
                UpdateCounts();
                if (task.Status == DownloadStatus.Completed)
                {
                    HandleTaskCompleted(task);
                }
            });
        };

        _queueManager.TaskCompleted += (s, task) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                HandleTaskCompleted(task);
            });
        };

        _queueManager.TaskRemoved += (s, task) =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                UntrackTask(task);
                ApplyFilter();
                UpdateCounts();
            });
        };

        _ipcService.OnDownloadRequested += payload =>
        {
            StatusText = $"Received download from browser: {payload.FileName ?? payload.Url}";
            ShowDownloadFileInfoDialog(
                payload.Url,
                payload.FileName ?? payload.PageTitle,
                !string.IsNullOrWhiteSpace(payload.Quality) ? $"YouTube ({payload.Quality})" : payload.PageTitle,
                payload.FileSize,
                payload.MimeType,
                payload.Quality,
                payload.Referer,
                payload.UserAgent,
                payload.Cookies,
                fromBrowser: true);
            UpdateCounts();
        };

        _ipcService.OnBatchDownloadRequested += payload =>
        {
            StatusText = $"Received {payload.Links.Count} selected link(s) from browser.";
            ShowBatchDownloadDialog(payload, fromBrowser: true);
            UpdateCounts();
        };

        _ipcService.OnMediaIntercepted += (media, item) =>
        {
            StatusText = $"Detected media stream: {media.Title ?? media.Url}";
        };

        _ipcService.OnStatusMessage += msg => StatusText = msg;

        // Commands
        AddDownloadCommand = new RelayCommand(ExecuteAddDownload);
        PauseCommand = new RelayCommand(async p => await ExecutePauseAsync(p as DownloadTask));
        StopCommand = new RelayCommand(async p => await ExecutePauseAsync(p as DownloadTask));
        ResumeCommand = new RelayCommand(async p => await ExecuteResumeAsync(p as DownloadTask));
        CancelCommand = new RelayCommand(async p => await ExecuteCancelAsync(p as DownloadTask));
        DeleteCommand = new RelayCommand(async p => await ExecuteDeleteAsync(p as DownloadTask));
        DeleteCompletedCommand = new RelayCommand(async () => await ExecuteDeleteCompletedAsync());
        PauseAllCommand = new RelayCommand(async () => await ExecutePauseAllAsync());
        StopAllCommand = new RelayCommand(async () => await ExecutePauseAllAsync());
        ResumeAllCommand = new RelayCommand(async () => await ExecuteResumeAllAsync());
        StartQueueCommand = new RelayCommand(async () => await ExecuteStartQueueAsync());
        StopQueueCommand = new RelayCommand(async () => await ExecuteStopQueueAsync());
        OptionsCommand = new RelayCommand(ExecuteOptions);
        SchedulerCommand = new RelayCommand(ExecuteScheduler);
        CopyUrlCommand = new RelayCommand(p => ExecuteCopyUrl(p as DownloadTask));
        OpenFileCommand = new RelayCommand(p => ExecuteOpenFile(p as DownloadTask));
        OpenFolderCommand = new RelayCommand(p => ExecuteOpenFolder(p as DownloadTask));
        InstallExtensionCommand = new RelayCommand(ExecuteInstallExtension);
        SelectCategoryCommand = new RelayCommand(cat => SelectedCategory = cat?.ToString() ?? "All");

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _speedTimer.Tick += (s, e) =>
        {
            TotalSpeedBytesPerSec = _queueManager.Tasks
                .Where(t => t.Status == DownloadStatus.Downloading)
                .Sum(t => t.SpeedBytesPerSec);
            UpdateCounts();
        };
        _speedTimer.Start();

        RegisterLoadedTasks();
        ApplyFilter();
    }

    public void RegisterLoadedTasks()
    {
        foreach (var task in _queueManager.Tasks)
        {
            if (task.Status == DownloadStatus.Completed)
            {
                // Avoid popping completion dialogs for tasks already finished in previous sessions
                _completionDialogShownIds.Add(task.Id);
            }
            TrackTaskCompletion(task);
        }
    }

    public void UpdateSelection(IReadOnlyList<DownloadTask> selected)
    {
        _selectedTasks.Clear();
        _selectedTasks.AddRange(selected);
        SelectedTask = selected.FirstOrDefault();
    }

    public void AssignSelectedToQueue(string queueId)
    {
        var targets = GetTargetTasks(null);
        if (targets.Count == 0) return;

        _schedulerService.AssignTasksToQueue(targets, queueId);
        ApplyFilter();
    }

    private List<DownloadTask> GetTargetTasks(DownloadTask? explicitTask)
    {
        if (explicitTask != null)
            return [explicitTask];
        if (_selectedTasks.Count > 0)
            return _selectedTasks.ToList();
        if (SelectedTask != null)
            return [SelectedTask];
        return [];
    }

    private void TrackTaskCompletion(DownloadTask task)
    {
        if (_trackedTaskIds.Add(task.Id))
        {
            task.PropertyChanged += OnTrackedTaskPropertyChanged;
        }
    }

    private void UntrackTask(DownloadTask task)
    {
        if (_trackedTaskIds.Remove(task.Id))
        {
            task.PropertyChanged -= OnTrackedTaskPropertyChanged;
        }
        _openProgressDialogs.Remove(task.Id);
    }

    private void OnTrackedTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DownloadTask task && e.PropertyName == nameof(DownloadTask.Status) && task.Status == DownloadStatus.Completed)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => HandleTaskCompleted(task));
        }
    }

    private void HandleTaskCompleted(DownloadTask task)
    {
        if (!_completionDialogShownIds.Add(task.Id))
            return;

        if (_openProgressDialogs.TryGetValue(task.Id, out var progressDlg))
        {
            _openProgressDialogs.Remove(task.Id);
            if (progressDlg.IsVisible)
            {
                progressDlg.Close();
            }
        }

        // Scheduled downloads run silently without popping up the Finish Download dialog
        var wasScheduled = _scheduledTaskIds.Remove(task.Id);

        if (UserSettingsService.Current.PlaySoundOnComplete)
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        }

        if (UserSettingsService.Current.AutoExtractArchives
            && !string.IsNullOrWhiteSpace(task.DestinationPath)
            && task.DestinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && File.Exists(task.DestinationPath))
        {
            var zipPath = task.DestinationPath;
            _ = Task.Run(() =>
            {
                try
                {
                    var extractDir = Path.Combine(
                        Path.GetDirectoryName(zipPath) ?? string.Empty,
                        Path.GetFileNameWithoutExtension(zipPath));
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
                }
                catch { }
            });
        }

        if (wasScheduled || !UserSettingsService.Current.ShowCompletedDialog)
            return;

        var doneDlg = new DownloadCompletedDialog(task);
        var mainWin = Application.Current?.MainWindow;
        _taskTargetScreens.Remove(task.Id, out var targetScreen);

        if (targetScreen != null)
        {
            WindowPositionHelper.CenterOnScreen(doneDlg, targetScreen);
            doneDlg.Topmost = true;
        }
        else if (WindowPositionHelper.IsWindowActuallyVisible(mainWin))
        {
            doneDlg.Owner = mainWin;
            doneDlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowPositionHelper.CenterOnActiveMonitor(doneDlg);
            doneDlg.Topmost = true;
        }
        doneDlg.Show();
    }

    private void UpdateCounts()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(QueuedCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(IsQueueRunning));
    }

    public void ApplyFilter()
    {
        FilteredTasks.Clear();
        var query = _queueManager.Tasks.AsEnumerable();

        if (SelectedCategory.StartsWith("Queue:", StringComparison.OrdinalIgnoreCase))
        {
            var queueId = SelectedCategory["Queue:".Length..];
            query = query.Where(t => string.Equals(t.QueueId, queueId, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            query = SelectedCategory switch
            {
                "Downloading" => query.Where(t => t.Status == DownloadStatus.Downloading),
                "Completed" or "Finished" => query.Where(t => t.Status == DownloadStatus.Completed),
                "Unfinished" => query.Where(t => t.Status != DownloadStatus.Completed),
                "Paused" => query.Where(t => t.Status == DownloadStatus.Paused),
                "Video" => query.Where(t => IsExtensionMatch(t.FileName, ".mp4", ".mkv", ".webm", ".ts", ".m3u8", ".avi", ".mov")),
                "Audio" or "Music" => query.Where(t => IsExtensionMatch(t.FileName, ".mp3", ".aac", ".flac", ".m4a", ".wav", ".ogg")),
                "Compressed" => query.Where(t => IsExtensionMatch(t.FileName, ".zip", ".rar", ".7z", ".tar", ".gz", ".iso")),
                "Documents" => query.Where(t => IsExtensionMatch(t.FileName, ".pdf", ".docx", ".doc", ".xlsx", ".pptx", ".txt")),
                "Programs" => query.Where(t => IsExtensionMatch(t.FileName, ".exe", ".msi", ".bat", ".cmd")),
                "GrabberProjects" => query.Where(t => IsExtensionMatch(t.FileName, ".m3u8", ".mpd", ".ts") || t.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)),
                "Queues" => query.Where(t => t.Status == DownloadStatus.Pending || t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Paused),
                _ => query
            };
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            query = query.Where(t =>
                t.FileName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                t.Url.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var task in query)
        {
            FilteredTasks.Add(task);
        }
    }

    private static bool IsExtensionMatch(string fileName, params string[] extensions)
    {
        var ext = Path.GetExtension(fileName);
        return extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    private void ExecuteAddDownload()
    {
        var clipboardText = string.Empty;
        try
        {
            if (Clipboard.ContainsText())
                clipboardText = Clipboard.GetText().Trim();
        }
        catch { }

        ShowDownloadFileInfoDialog(clipboardText);
    }

    private void ShowDownloadFileInfoDialog(
        string? url,
        string? suggestedFileName = null,
        string? description = null,
        long? fileSize = null,
        string? mimeType = null,
        string? quality = null,
        string? referer = null,
        string? userAgent = null,
        string? cookies = null,
        bool fromBrowser = false)
    {
        var preferredQueueId = SelectedCategory.StartsWith("Queue:", StringComparison.OrdinalIgnoreCase)
            ? SelectedCategory["Queue:".Length..]
            : ScheduleQueue.MainQueueId;

        var mainWin = Application.Current?.MainWindow;
        var activeScreen = fromBrowser ? WindowPositionHelper.CaptureActiveMonitor() : null;

        var dialog = new DownloadFileInfoDialog(
            url,
            suggestedFileName,
            description,
            fileSize,
            mimeType,
            quality,
            _schedulerService,
            preferredQueueId);

        if (fromBrowser || !WindowPositionHelper.IsWindowActuallyVisible(mainWin))
        {
            activeScreen ??= WindowPositionHelper.CaptureActiveMonitor();
            WindowPositionHelper.CenterOnScreen(dialog, activeScreen);
            dialog.Topmost = true;
        }
        else
        {
            dialog.Owner = mainWin;
        }

        if (dialog.ShowDialog() != true)
            return;

        var targetUrl = dialog.DownloadUrl;
        var destPath = UserSettingsService.ResolveDuplicatePath(dialog.DestinationPath);
        var startNow = !dialog.DownloadLater;
        var category = dialog.SelectedCategory;
        var queueId = dialog.SelectedQueueId;
        var desc = dialog.DescriptionText;

        var folder = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);

        if (YtDlpService.IsSupportedVideoPage(targetUrl))
        {
            var title = Path.GetFileNameWithoutExtension(destPath);
            var ytTask = YtDlpService.StartDownload(
                _queueManager,
                targetUrl,
                destPath,
                title,
                quality ?? "best",
                msg => StatusText = msg,
                desc,
                category,
                startNow);

            if (ytTask != null)
            {
                ytTask.QueueId = queueId;
                if (activeScreen != null) _taskTargetScreens[ytTask.Id] = activeScreen;
                _ = _queueManager.SaveStateAsync();
                if (startNow)
                    OpenProgressDialog(ytTask, targetScreen: activeScreen);
                else
                    StatusText = $"Scheduled in '{_schedulerService.GetQueueOrDefault(queueId).Name}': {ytTask.FileName}";
            }
            return;
        }

        var isHls = targetUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            || (mimeType?.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) == true);

        if (isHls)
        {
            if (destPath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                destPath = Path.ChangeExtension(destPath, ".mp4");

            var hlsTask = new DownloadTask
            {
                Url = targetUrl,
                DestinationPath = destPath,
                FileName = Path.GetFileName(destPath),
                Status = startNow ? DownloadStatus.Downloading : DownloadStatus.Paused,
                IsExternalManaged = true,
                Category = category,
                QueueId = queueId,
                Description = string.IsNullOrWhiteSpace(desc) ? "HLS Stream" : desc
            };
            _queueManager.AddTask(hlsTask);
            if (activeScreen != null) _taskTargetScreens[hlsTask.Id] = activeScreen;

            if (!startNow)
            {
                StatusText = $"Scheduled in '{_schedulerService.GetQueueOrDefault(queueId).Name}': {hlsTask.FileName}";
                return;
            }

            OpenProgressDialog(hlsTask, targetScreen: activeScreen);
            StartHlsDownload(hlsTask, referer, userAgent, cookies);
            return;
        }

        _ = Task.Run(async () =>
        {
            var task = await _queueManager.AddTaskAsync(
                targetUrl,
                destPath,
                Path.GetFileName(destPath),
                startImmediately: startNow,
                queueId: queueId);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                task.Category = category;
                if (!string.IsNullOrWhiteSpace(desc))
                    task.Description = desc;
                if (activeScreen != null) _taskTargetScreens[task.Id] = activeScreen;
                _ = _queueManager.SaveStateAsync();

                if (!startNow)
                {
                    StatusText = $"Scheduled in '{_schedulerService.GetQueueOrDefault(queueId).Name}': {task.FileName}";
                }
                else
                {
                    StatusText = $"Added download: {task.FileName}";
                    OpenProgressDialog(task, targetScreen: activeScreen);
                }
            });
        });
    }

    public void OpenBatchDownloadFromOfflineQueue(ExtensionBatchDownloadPayload payload)
    {
        ShowBatchDownloadDialog(payload, fromBrowser: true);
        UpdateCounts();
    }

    private void ShowBatchDownloadDialog(ExtensionBatchDownloadPayload payload, bool fromBrowser = false)
    {
        var preferredQueueId = SelectedCategory.StartsWith("Queue:", StringComparison.OrdinalIgnoreCase)
            ? SelectedCategory["Queue:".Length..]
            : ScheduleQueue.MainQueueId;

        var mainWin = Application.Current?.MainWindow;
        var activeScreen = fromBrowser ? WindowPositionHelper.CaptureActiveMonitor() : null;
        var dlg = new BatchDownloadDialog(payload, _schedulerService, _queueManager, preferredQueueId);
        if (fromBrowser || !WindowPositionHelper.IsWindowActuallyVisible(mainWin))
        {
            activeScreen ??= WindowPositionHelper.CaptureActiveMonitor();
            WindowPositionHelper.CenterOnScreen(dlg, activeScreen);
            dlg.Topmost = true;
        }
        else
        {
            dlg.Owner = mainWin;
        }

        if (dlg.ShowDialog() != true)
            return;

        var selected = dlg.SelectedItems;
        if (selected.Count == 0) return;

        var startNow = !dlg.DownloadLater;
        var queueId = dlg.SelectedQueueId;
        var useLinkText = dlg.UseLinkTextAsDescription;
        var queueName = _schedulerService.GetQueueOrDefault(queueId).Name;

        _ = Task.Run(async () =>
        {
            foreach (var item in selected)
            {
                try
                {
                    var resolvedDest = UserSettingsService.ResolveDuplicatePath(item.DestinationPath);
                    var folder = Path.GetDirectoryName(resolvedDest);
                    if (!string.IsNullOrWhiteSpace(folder))
                        Directory.CreateDirectory(folder);

                    var desc = useLinkText ? item.LinkText : payload.PageTitle;

                    if (YtDlpService.IsSupportedVideoPage(item.Url))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            var ytTask = YtDlpService.StartDownload(
                                _queueManager,
                                item.Url,
                                resolvedDest,
                                Path.GetFileNameWithoutExtension(resolvedDest),
                                "best",
                                msg => StatusText = msg,
                                desc,
                                item.Category,
                                startNow);

                            if (ytTask != null)
                            {
                                ytTask.QueueId = queueId;
                                if (activeScreen != null) _taskTargetScreens[ytTask.Id] = activeScreen;
                                _ = _queueManager.SaveStateAsync();
                            }
                        });
                        continue;
                    }

                    var task = await _queueManager.AddTaskAsync(
                        item.Url,
                        resolvedDest,
                        Path.GetFileName(resolvedDest),
                        startImmediately: startNow,
                        queueId: queueId);

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        task.Category = item.Category;
                        if (!string.IsNullOrWhiteSpace(desc))
                            task.Description = desc;
                        if (activeScreen != null) _taskTargetScreens[task.Id] = activeScreen;
                    });
                }
                catch
                {
                    // Continue adding remaining batch items
                }
            }

            await _queueManager.SaveStateAsync();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ApplyFilter();
                UpdateCounts();
                StatusText = startNow
                    ? $"Started {selected.Count} batch download(s) in '{queueName}'."
                    : $"Added {selected.Count} file(s) to '{queueName}' schedule.";
            });
        });
    }

    private Task StartExternalOrMediaTaskAsync(DownloadTask task)
    {
        if (task.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            task.IsExternalManaged = true;
            task.Status = DownloadStatus.Downloading;
            task.ErrorMessage = null;
            StartHlsDownload(task);
            return Task.CompletedTask;
        }

        task.IsExternalManaged = true;
        YtDlpService.ResumeDownload(_queueManager, task, msg => StatusText = msg);
        return Task.CompletedTask;
    }

    private void StartHlsDownload(DownloadTask hlsTask, string? referer = null, string? userAgent = null, string? cookies = null)
    {
        var hlsOptions = new Media.Downloading.HlsDownloadOptions();
        if (!string.IsNullOrWhiteSpace(referer)) hlsOptions.Headers["Referer"] = referer;
        if (!string.IsNullOrWhiteSpace(userAgent)) hlsOptions.Headers["User-Agent"] = userAgent;
        if (!string.IsNullOrWhiteSpace(cookies)) hlsOptions.Headers["Cookie"] = cookies;

        _ = Task.Run(async () =>
        {
            try
            {
                var res = await _mediaPipeline.DownloadHlsStreamAsync(hlsTask.Url, hlsTask.DestinationPath, options: hlsOptions);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (res.Success)
                    {
                        if (File.Exists(hlsTask.DestinationPath))
                        {
                            var len = new FileInfo(hlsTask.DestinationPath).Length;
                            hlsTask.TotalBytes = len;
                            hlsTask.DownloadedBytes = len;
                        }
                        hlsTask.CompletedAt = DateTimeOffset.UtcNow;
                        hlsTask.Status = DownloadStatus.Completed;
                        StatusText = $"HLS stream saved: {hlsTask.FileName}";
                    }
                    else
                    {
                        hlsTask.Status = DownloadStatus.Failed;
                        hlsTask.ErrorMessage = res.Error ?? "HLS download failed";
                        StatusText = $"HLS failed: {hlsTask.ErrorMessage}";
                    }
                    _ = _queueManager.SaveStateAsync();
                });
            }
            catch (Exception ex)
            {
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    hlsTask.Status = DownloadStatus.Failed;
                    hlsTask.ErrorMessage = ex.Message;
                    StatusText = $"HLS error: {ex.Message}";
                    _ = _queueManager.SaveStateAsync();
                });
            }
        });
    }

    public void OpenProgressDialog(DownloadTask task, bool startMinimized = false, System.Windows.Forms.Screen? targetScreen = null)
    {
        if (_openProgressDialogs.TryGetValue(task.Id, out var existing) && existing.IsVisible)
        {
            if (!startMinimized)
                existing.Activate();
            return;
        }

        var mainWin = Application.Current?.MainWindow;
        var dlg = new DownloadProgressDialog(
            task,
            t => ExecutePauseAsync(t),
            t => ExecuteResumeAsync(t),
            t => ExecuteCancelAsync(t));

        _openProgressDialogs[task.Id] = dlg;
        dlg.Closed += (_, _) => _openProgressDialogs.Remove(task.Id);

        if (startMinimized)
        {
            dlg.ShowActivated = false;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.WindowState = WindowState.Minimized;
        }
        else if (targetScreen != null)
        {
            WindowPositionHelper.CenterOnScreen(dlg, targetScreen);
        }
        else if (WindowPositionHelper.IsWindowActuallyVisible(mainWin))
        {
            dlg.Owner = mainWin;
        }
        else
        {
            WindowPositionHelper.CenterOnActiveMonitor(dlg);
        }

        dlg.Show();
    }

    private async Task ExecutePauseAsync(DownloadTask? task)
    {
        var targets = GetTargetTasks(task);
        foreach (var t in targets)
        {
            if (t.IsExternalManaged)
                YtDlpService.PauseDownload(t.Id);

            await _queueManager.PauseTaskAsync(t.Id);
        }

        if (targets.Count == 1)
            StatusText = $"Paused: {targets[0].FileName}";
        else if (targets.Count > 1)
            StatusText = $"Paused {targets.Count} downloads.";
    }

    private async Task ExecuteResumeAsync(DownloadTask? task)
    {
        var targets = GetTargetTasks(task).Where(t => t.Status != DownloadStatus.Completed).ToList();
        foreach (var t in targets)
        {
            if (t.IsExternalManaged || YtDlpService.IsSupportedVideoPage(t.Url))
            {
                t.IsExternalManaged = true;
                YtDlpService.ResumeDownload(_queueManager, t, msg => StatusText = msg);
                continue;
            }

            await _queueManager.ResumeTaskAsync(t.Id);
        }

        if (targets.Count == 1)
            StatusText = $"Resumed: {targets[0].FileName}";
        else if (targets.Count > 1)
            StatusText = $"Resumed {targets.Count} downloads.";
    }

    private async Task ExecuteCancelAsync(DownloadTask? task)
    {
        var targets = GetTargetTasks(task);
        foreach (var t in targets)
        {
            if (t.IsExternalManaged)
                YtDlpService.PauseDownload(t.Id);
            await _queueManager.CancelTaskAsync(t.Id);
            StatusText = $"Cancelled: {t.FileName}";
        }
    }

    private async Task ExecuteDeleteAsync(DownloadTask? task)
    {
        var targets = GetTargetTasks(task);
        if (targets.Count == 0) return;

        var confirmDlg = new DeleteConfirmDialog(targets);
        var mainWin = Application.Current?.MainWindow;
        if (mainWin != null && mainWin.IsVisible)
            confirmDlg.Owner = mainWin;
        else
            confirmDlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (confirmDlg.ShowDialog() != true || confirmDlg.Choice == DeleteChoice.Cancel)
            return;

        var deleteFiles = confirmDlg.Choice == DeleteChoice.DeleteFileAndList;

        foreach (var t in targets)
        {
            if (t.IsExternalManaged)
                YtDlpService.PauseDownload(t.Id);

            if (_openProgressDialogs.TryGetValue(t.Id, out var dlg) && dlg.IsVisible)
                dlg.Close();
        }

        await _queueManager.RemoveTasksAsync(targets.Select(t => t.Id), deleteFile: deleteFiles);
        _selectedTasks.Clear();
        SelectedTask = null;
        ApplyFilter();

        StatusText = deleteFiles
            ? $"Deleted {targets.Count} download(s) from list and disk."
            : $"Removed {targets.Count} download(s) from list.";
    }

    private async Task ExecutePauseAllAsync()
    {
        foreach (var t in _queueManager.Tasks.Where(t => t.Status == DownloadStatus.Downloading).ToList())
        {
            if (t.IsExternalManaged)
                YtDlpService.PauseDownload(t.Id);
            await _queueManager.PauseTaskAsync(t.Id);
        }
        StatusText = "Paused all active downloads.";
    }

    private async Task ExecuteResumeAllAsync()
    {
        foreach (var t in _queueManager.Tasks.Where(t => t.Status == DownloadStatus.Paused).ToList())
        {
            await ExecuteResumeAsync(t);
        }
        StatusText = "Resumed all paused downloads.";
    }

    private void ExecuteOpenFile(DownloadTask? task)
    {
        task ??= SelectedTask;
        if (task == null) return;

        if (string.IsNullOrWhiteSpace(task.DestinationPath) || !File.Exists(task.DestinationPath))
        {
            MessageBox.Show("This file was moved or deleted from its original folder.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(task.DestinationPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteOpenFolder(DownloadTask? task)
    {
        task ??= SelectedTask;
        var folder = task != null && !string.IsNullOrEmpty(task.DestinationPath)
            ? Path.GetDirectoryName(task.DestinationPath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
    }

    private void ExecuteInstallExtension()
    {
        var dialog = new BrowserIntegrationDialog
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        StatusText = "Browser integration check completed.";
    }

    private async Task ExecuteDeleteCompletedAsync()
    {
        var completed = _queueManager.Tasks.Where(t => t.Status == DownloadStatus.Completed).ToList();
        if (completed.Count == 0)
        {
            StatusText = "No completed downloads to remove.";
            return;
        }

        await _queueManager.RemoveTasksAsync(completed.Select(t => t.Id), deleteFile: false);

        ApplyFilter();
        UpdateCounts();
        StatusText = $"Cleared {completed.Count} completed download(s) from list.";
    }

    public async Task StartQueueByIdAsync(string queueId)
    {
        await _schedulerService.StartQueueAsync(queueId);
        UpdateCounts();
    }

    public async Task StopQueueByIdAsync(string queueId)
    {
        await _schedulerService.StopQueueAsync(queueId);
        UpdateCounts();
    }

    private async Task ExecuteStartQueueAsync()
    {
        if (SelectedCategory.StartsWith("Queue:", StringComparison.OrdinalIgnoreCase))
        {
            var queueId = SelectedCategory["Queue:".Length..];
            await _schedulerService.StartQueueAsync(queueId);
        }
        else
        {
            await _schedulerService.StartAllWaitingQueuesAsync();
        }
        UpdateCounts();
    }

    private async Task ExecuteStopQueueAsync()
    {
        if (SelectedCategory.StartsWith("Queue:", StringComparison.OrdinalIgnoreCase))
        {
            var queueId = SelectedCategory["Queue:".Length..];
            await _schedulerService.StopQueueAsync(queueId);
        }
        else
        {
            await _schedulerService.StopAllRunningQueuesAsync();
            await ExecutePauseAllAsync();
        }
        UpdateCounts();
    }

    private void ExecuteOptions()
    {
        var dialog = new OptionsDialog(_queueManager.MaxConcurrentDownloads)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() == true)
        {
            UserSettingsService.ApplyToDownloaderOptions(_queueManager.Options);
            _queueManager.MaxConcurrentDownloads = dialog.MaxConcurrentDownloads;
            _queueManager.RefreshNetworkClient();
            StatusText = $"Options saved (Concurrent: {_queueManager.MaxConcurrentDownloads}, Segments: {_queueManager.Options.DefaultSegments}).";
        }
    }

    private void ExecuteScheduler()
    {
        var initialQueueId = SelectedCategory.StartsWith("Queue:", StringComparison.OrdinalIgnoreCase)
            ? SelectedCategory["Queue:".Length..]
            : ScheduleQueue.MainQueueId;

        var dialog = new SchedulerDialog(_schedulerService, _queueManager, initialQueueId, _selectedTasks.ToList())
        {
            Owner = Application.Current?.MainWindow
        };
        dialog.ShowDialog();
        ApplyFilter();
        UpdateCounts();
    }

    private void ExecuteCopyUrl(DownloadTask? task)
    {
        task ??= SelectedTask;
        if (task != null && !string.IsNullOrWhiteSpace(task.Url))
        {
            Clipboard.SetText(task.Url);
            StatusText = $"Copied URL for {task.FileName}";
        }
    }

    public void Dispose()
    {
        _speedTimer.Stop();
        _schedulerService.Dispose();
        foreach (var task in _queueManager.Tasks)
        {
            task.PropertyChanged -= OnTrackedTaskPropertyChanged;
        }
        GC.SuppressFinalize(this);
    }
}
