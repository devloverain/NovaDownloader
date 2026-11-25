using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NovaDownloader.Core.Configuration;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Queue;
using NovaDownloader.Media;
using NovaDownloader.UI.Services;
using NovaDownloader.UI.ViewModels;

namespace NovaDownloader.UI;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly IDownloadQueueManager _queueManager;
    private readonly IMediaStreamPipeline _mediaPipeline;
    private readonly IpcService _ipcService;
    private readonly MainViewModel _viewModel;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _isExplicitExit;

    public MainWindow() : this(startMinimized: false)
    {
    }

    public MainWindow(bool startMinimized)
    {
        InitializeComponent();
        InitializeTrayIcon();

        // 1. Resolve absolute state path in AppData (safe for Windows Startup working directory)
        var stateFilePath = ResolveAndMigrateStateFilePath();
        var options = new DownloaderOptions { StateFilePath = stateFilePath };
        UserSettingsService.ApplyToDownloaderOptions(options);

        _queueManager = new DownloadQueueManager(options);
        _queueManager.MaxConcurrentDownloads = UserSettingsService.Current.MaxConcurrentDownloads;
        _ = InitializeQueueAsync();

        // 2. Initialize Media Stream Pipeline (HLS/DASH/FFmpeg)
        _mediaPipeline = new MediaStreamPipeline();

        // 3. Initialize Named Pipe IPC for browser extension & single-instance wakeup
        _ipcService = new IpcService(_queueManager, _mediaPipeline);
        _ipcService.OnShowWindowRequested += RestoreFromTray;
        _ipcService.Start();

        // 4. Initialize ViewModel and bind
        _viewModel = new MainViewModel(_queueManager, _mediaPipeline, _ipcService);
        _viewModel.SchedulerService.QueuesChanged += (_, _) => Dispatcher.BeginInvoke(RefreshQueueTreeItems);
        DataContext = _viewModel;
        RefreshQueueTreeItems();

        // 5. Process any queued downloads sent while the app was offline
        _ = ProcessOfflineQueueAsync();

        if (startMinimized)
        {
            Hide();
        }
    }

    private static string ResolveAndMigrateStateFilePath()
    {
        var appDataDir = UserSettingsService.AppDataDirectory;
        Directory.CreateDirectory(appDataDir);
        var targetPath = Path.Combine(appDataDir, "downloads.json");

        if (!File.Exists(targetPath))
        {
            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "downloads.json"),
                Path.Combine(Environment.CurrentDirectory, "downloads.json")
            };

            foreach (var legacy in candidates)
            {
                try
                {
                    if (File.Exists(legacy))
                    {
                        File.Copy(legacy, targetPath, overwrite: false);
                        break;
                    }
                }
                catch
                {
                    // Ignore migration failure
                }
            }
        }

        return targetPath;
    }

    private async Task InitializeQueueAsync()
    {
        await _queueManager.LoadStateAsync();
        Dispatcher.Invoke(() =>
        {
            _viewModel?.RegisterLoadedTasks();
            _viewModel?.ApplyFilter();
        });
        await _queueManager.StartAsync();
    }

    private void RefreshQueueTreeItems()
    {
        if (QueuesTreeItem == null || _viewModel == null) return;

        QueuesTreeItem.Items.Clear();
        foreach (var queue in _viewModel.SchedulerService.Queues)
        {
            var item = new TreeViewItem
            {
                Header = queue.Name,
                Tag = $"Queue:{queue.Id}",
                ToolTip = queue.ScheduleSummary
            };
            QueuesTreeItem.Items.Add(item);
        }
    }

    private void InitializeTrayIcon()
    {
        System.Drawing.Icon? trayIco = null;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/Icons/app_logo.png", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                using var bmp = new System.Drawing.Bitmap(streamInfo.Stream);
                trayIco = System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }
        catch { }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open NovaDownloader", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Add URL...", null, (_, _) =>
        {
            RestoreFromTray();
            _viewModel.AddDownloadCommand.Execute(null);
        });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var trayStartQueueItem = new System.Windows.Forms.ToolStripMenuItem("Start Queue");
        var trayStopQueueItem = new System.Windows.Forms.ToolStripMenuItem("Stop Queue");
        menu.Items.Add(trayStartQueueItem);
        menu.Items.Add(trayStopQueueItem);

        menu.Opening += (_, _) =>
        {
            if (_viewModel == null) return;
            trayStartQueueItem.DropDownItems.Clear();
            trayStopQueueItem.DropDownItems.Clear();

            foreach (var q in _viewModel.SchedulerService.Queues)
            {
                var queueId = q.Id;
                var pending = _queueManager.Tasks.Count(t =>
                    string.Equals(t.QueueId, q.Id, StringComparison.OrdinalIgnoreCase)
                    && t.Status != DownloadStatus.Completed);
                var active = _queueManager.Tasks.Count(t =>
                    string.Equals(t.QueueId, q.Id, StringComparison.OrdinalIgnoreCase)
                    && (t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending));

                trayStartQueueItem.DropDownItems.Add($"{q.Name} ({pending} pending)", null,
                    (_, _) => Dispatcher.BeginInvoke(async () => await _viewModel.StartQueueByIdAsync(queueId)));
                trayStopQueueItem.DropDownItems.Add($"{q.Name} ({active} active)", null,
                    (_, _) => Dispatcher.BeginInvoke(async () => await _viewModel.StopQueueByIdAsync(queueId)));
            }

            if (_viewModel.SchedulerService.Queues.Count > 1)
            {
                trayStartQueueItem.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                trayStartQueueItem.DropDownItems.Add("Start All Queues", null,
                    (_, _) => Dispatcher.BeginInvoke(() => _viewModel.StartQueueCommand.Execute(null)));

                trayStopQueueItem.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
                trayStopQueueItem.DropDownItems.Add("Stop All Queues", null,
                    (_, _) => Dispatcher.BeginInvoke(() => _viewModel.StopQueueCommand.Execute(null)));
            }
        };

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit NovaDownloader", null, (_, _) => ExitApplication());

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayIco ?? System.Drawing.SystemIcons.Application,
            Text = "NovaDownloader",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
        });
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Hide();

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
        => ExitApplication();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _viewModel.Dispose();
        _ipcService.Dispose();
        _queueManager.Dispose();
        base.OnClosing(e);
    }

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_viewModel is null) return;
        if (e.NewValue is TreeViewItem item && item.Tag is string category)
        {
            _viewModel.SelectedCategory = category;
        }
    }

    private void DownloadsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        var selected = DownloadsGrid.SelectedItems.OfType<DownloadTask>().ToList();
        _viewModel.UpdateSelection(selected);
    }

    private void DownloadsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (MoveToQueueMenuItem == null || _viewModel == null) return;

        MoveToQueueMenuItem.Items.Clear();
        foreach (var queue in _viewModel.SchedulerService.Queues)
        {
            var mi = new MenuItem
            {
                Header = queue.Name,
                Tag = queue.Id
            };
            mi.Click += (_, _) => _viewModel.AssignSelectedToQueue(queue.Id);
            MoveToQueueMenuItem.Items.Add(mi);
        }
    }

    private void StartQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button btn) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (var queue in _viewModel.SchedulerService.Queues)
        {
            var pendingCount = _queueManager.Tasks.Count(t =>
                string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase)
                && t.Status != DownloadStatus.Completed);

            var statusSuffix = queue.IsRunning ? " (Running)" : $" ({pendingCount} pending)";
            var mi = new MenuItem { Header = $"▶  {queue.Name}{statusSuffix}" };
            var queueId = queue.Id;
            mi.Click += async (_, _) => await _viewModel.StartQueueByIdAsync(queueId);
            menu.Items.Add(mi);
        }

        if (_viewModel.SchedulerService.Queues.Count > 1)
        {
            menu.Items.Add(new Separator());
            var allItem = new MenuItem { Header = "▶  Start All Queues" };
            allItem.Click += (_, _) => _viewModel.StartQueueCommand.Execute(null);
            menu.Items.Add(allItem);
        }

        menu.IsOpen = true;
    }

    private void StopQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || sender is not Button btn) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        foreach (var queue in _viewModel.SchedulerService.Queues)
        {
            var activeCount = _queueManager.Tasks.Count(t =>
                string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase)
                && (t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending));

            var statusSuffix = queue.IsRunning || activeCount > 0 ? $" ({activeCount} active)" : " (Idle)";
            var mi = new MenuItem { Header = $"■  {queue.Name}{statusSuffix}" };
            var queueId = queue.Id;
            mi.Click += async (_, _) => await _viewModel.StopQueueByIdAsync(queueId);
            menu.Items.Add(mi);
        }

        if (_viewModel.SchedulerService.Queues.Count > 1)
        {
            menu.Items.Add(new Separator());
            var allItem = new MenuItem { Header = "■  Stop All Queues" };
            allItem.Click += (_, _) => _viewModel.StopQueueCommand.Execute(null);
            menu.Items.Add(allItem);
        }

        menu.IsOpen = true;
    }

    private async Task ProcessOfflineQueueAsync()
    {
        try
        {
            var queueDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NovaDownloader", "Queue");
            if (!Directory.Exists(queueDir)) return;

            var pendingFiles = Directory.GetFiles(queueDir, "pending_*.json");
            foreach (var file in pendingFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var envelope = JsonSerializer.Deserialize<ExtensionEnvelope>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (envelope?.Payload.HasValue == true)
                    {
                        if (string.Equals(envelope.Action, "BATCH_DOWNLOAD_LINKS", StringComparison.OrdinalIgnoreCase))
                        {
                            var batchPayload = envelope.Payload.Value.Deserialize<ExtensionBatchDownloadPayload>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (batchPayload?.Links is { Count: > 0 })
                            {
                                _ = Dispatcher.BeginInvoke(() => _viewModel.OpenBatchDownloadFromOfflineQueue(batchPayload));
                            }
                        }
                        else
                        {
                            var payload = envelope.Payload.Value.Deserialize<ExtensionDownloadPayload>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (payload != null && !string.IsNullOrWhiteSpace(payload.Url))
                            {
                                var defaultDest = Path.Combine(
                                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                    "Downloads",
                                    payload.FileName ?? "download_" + DateTime.Now.Ticks);

                                await _queueManager.AddTaskAsync(payload.Url, defaultDest, payload.FileName);
                            }
                        }
                    }
                    File.Delete(file);
                }
                catch
                {
                    // Ignore corrupted item
                }
            }
        }
        catch
        {
            // Ignore
        }
    }
}
