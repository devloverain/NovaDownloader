using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaDownloader.Core.Configuration;
using NovaDownloader.Core.Engine;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Utils;

namespace NovaDownloader.Core.Queue;

public class DownloadQueueManager : IDownloadQueueManager
{
    private readonly IDownloadEngine _engine;
    private readonly DownloaderOptions _options;
    private readonly bool _ownsEngine;
    private readonly List<DownloadTask> _tasks = [];
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCts = new();
    private readonly Lock _taskLock = new();
    private readonly SemaphoreSlim _queueSignal = new(0, int.MaxValue);

    private CancellationTokenSource? _dispatcherCts;
    private Task? _dispatcherTask;
    private bool _isRunning;
    private int _maxConcurrentDownloads;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public event EventHandler<DownloadTask>? TaskAdded;
    public event EventHandler<DownloadTask>? TaskStatusChanged;
    public event EventHandler<DownloadProgressEventArgs>? TaskProgressUpdated;
    public event EventHandler<DownloadTask>? TaskCompleted;
    public event EventHandler<(DownloadTask Task, Exception Error)>? TaskFailed;
    public event EventHandler<DownloadTask>? TaskRemoved;

    public IReadOnlyList<DownloadTask> Tasks
    {
        get
        {
            lock (_taskLock)
            {
                return _tasks.ToList();
            }
        }
    }

    public int MaxConcurrentDownloads
    {
        get => _maxConcurrentDownloads;
        set
        {
            _maxConcurrentDownloads = Math.Max(1, value);
            _queueSignal.Release();
        }
    }

    public DownloaderOptions Options => _options;

    public bool IsRunning => _isRunning;

    public void RefreshNetworkClient()
    {
        if (_engine is MultiSegmentDownloader msd)
        {
            msd.RefreshHttpClient();
        }
    }

    public DownloadQueueManager(DownloaderOptions? options = null, IDownloadEngine? engine = null)
    {
        _options = options ?? new DownloaderOptions();
        _maxConcurrentDownloads = _options.MaxConcurrentTasks;

        if (engine != null)
        {
            _engine = engine;
            _ownsEngine = false;
        }
        else
        {
            _engine = new MultiSegmentDownloader(_options);
            _ownsEngine = true;
        }

        _engine.ProgressUpdated += (sender, args) => TaskProgressUpdated?.Invoke(this, args);
        _engine.TaskCompleted += (sender, task) =>
        {
            TaskCompleted?.Invoke(this, task);
            TaskStatusChanged?.Invoke(this, task);
        };
        _engine.TaskFailed += (sender, tuple) =>
        {
            TaskFailed?.Invoke(this, tuple);
            TaskStatusChanged?.Invoke(this, tuple.Task);
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return;

        _isRunning = true;
        _dispatcherCts = new CancellationTokenSource();
        _dispatcherTask = ProcessQueueLoopAsync(_dispatcherCts.Token);
        _queueSignal.Release();

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning) return;

        _isRunning = false;

        // Cancel queue loop
        if (_dispatcherCts != null)
        {
            await _dispatcherCts.CancelAsync();
        }

        // Pause all actively running downloads
        var activeIds = _activeCts.Keys.ToList();
        var pauseTasks = activeIds.Select(id => PauseTaskAsync(id));
        await Task.WhenAll(pauseTasks);

        if (_dispatcherTask != null)
        {
            try
            {
                await _dispatcherTask;
            }
            catch (OperationCanceledException) { }
        }

        await SaveStateAsync();
    }

    public Task<DownloadTask> AddTaskAsync(
        string url,
        string destinationPath,
        string? fileName = null,
        CancellationToken cancellationToken = default)
        => AddTaskAsync(url, destinationPath, fileName, startImmediately: true, queueId: "main", cancellationToken);

    public async Task<DownloadTask> AddTaskAsync(
        string url,
        string destinationPath,
        string? fileName,
        bool startImmediately,
        string queueId = "main",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var task = new DownloadTask
        {
            Id = Guid.NewGuid(),
            Url = url,
            DestinationPath = destinationPath,
            FileName = fileName ?? string.Empty,
            QueueId = string.IsNullOrWhiteSpace(queueId) ? "main" : queueId,
            Status = startImmediately ? DownloadStatus.Pending : DownloadStatus.Paused,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _engine.InitializeTaskAsync(task, cancellationToken);
        AddTask(task);
        return task;
    }

    public void AddTask(DownloadTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        lock (_taskLock)
        {
            if (_tasks.Any(t => t.Id == task.Id))
            {
                throw new InvalidOperationException($"Task with ID '{task.Id}' already exists.");
            }

            _tasks.Add(task);
        }

        TaskAdded?.Invoke(this, task);
        TaskStatusChanged?.Invoke(this, task);

        _queueSignal.Release();
        _ = SaveStateAsync();
    }

    public async Task PauseTaskAsync(Guid taskId)
    {
        DownloadTask? task;
        lock (_taskLock)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
        }

        if (task == null) return;

        if (_activeCts.TryRemove(taskId, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        task.Status = DownloadStatus.Paused;
        task.SpeedBytesPerSec = 0;
        task.EstimatedTimeRemaining = null;

        TaskStatusChanged?.Invoke(this, task);
        _queueSignal.Release();
        await SaveStateAsync();
    }

    public async Task ResumeTaskAsync(Guid taskId)
    {
        DownloadTask? task;
        lock (_taskLock)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
        }

        if (task == null || task.Status == DownloadStatus.Completed) return;

        task.Status = DownloadStatus.Pending;
        task.ErrorMessage = null;

        TaskStatusChanged?.Invoke(this, task);
        _queueSignal.Release();
        await SaveStateAsync();
    }

    public async Task CancelTaskAsync(Guid taskId)
    {
        DownloadTask? task;
        lock (_taskLock)
        {
            task = _tasks.FirstOrDefault(t => t.Id == taskId);
        }

        if (task == null) return;

        if (_activeCts.TryRemove(taskId, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        task.Status = DownloadStatus.Cancelled;
        task.SpeedBytesPerSec = 0;
        task.EstimatedTimeRemaining = null;

        TaskStatusChanged?.Invoke(this, task);
        _queueSignal.Release();
        await SaveStateAsync();
    }

    public async Task RemoveTaskAsync(Guid taskId, bool deleteFile = false)
    {
        await RemoveTasksAsync([taskId], deleteFile);
    }

    public async Task RemoveTasksAsync(IEnumerable<Guid> taskIds, bool deleteFile = false)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        var idSet = taskIds.ToHashSet();
        if (idSet.Count == 0) return;

        var removedTasks = new List<DownloadTask>();
        lock (_taskLock)
        {
            for (int i = _tasks.Count - 1; i >= 0; i--)
            {
                if (idSet.Contains(_tasks[i].Id))
                {
                    removedTasks.Add(_tasks[i]);
                    _tasks.RemoveAt(i);
                }
            }
        }

        if (removedTasks.Count == 0) return;

        foreach (var task in removedTasks)
        {
            if (_activeCts.TryRemove(task.Id, out var cts))
            {
                await cts.CancelAsync();
                cts.Dispose();
            }

            if (deleteFile)
            {
                TryDeleteTaskFilesSafe(task);
            }

            TaskRemoved?.Invoke(this, task);
        }

        _queueSignal.Release();
        await SaveStateAsync();
    }

    private static void TryDeleteTaskFilesSafe(DownloadTask task)
    {
        TryDeleteFileSafe(task.DestinationPath);
        TryDeleteFileSafe(task.IncompleteFilePath);

        if (!string.IsNullOrWhiteSpace(task.DestinationPath))
        {
            TryDeleteFileSafe(task.DestinationPath + ".part");
            TryDeleteFileSafe(task.DestinationPath + ".ytdl");
        }
    }

    private static void TryDeleteFileSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Suppress deletion errors if file was moved, locked, or path is invalid
        }
    }

    private async Task ProcessQueueLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_isRunning) break;

            while (_activeCts.Count < _maxConcurrentDownloads)
            {
                DownloadTask? nextTask = null;
                lock (_taskLock)
                {
                    nextTask = _tasks.FirstOrDefault(t => !t.IsExternalManaged && t.Status == DownloadStatus.Pending && !_activeCts.ContainsKey(t.Id));
                }

                if (nextTask == null)
                {
                    break;
                }

                StartTaskDownload(nextTask);
            }
        }
    }

    private void StartTaskDownload(DownloadTask task)
    {
        var taskCts = new CancellationTokenSource();
        if (!_activeCts.TryAdd(task.Id, taskCts))
        {
            taskCts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                TaskStatusChanged?.Invoke(this, task);
                await _engine.DownloadAsync(task, taskCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Normal pause / cancel
            }
            catch (Exception)
            {
                // Engine handles failure event and status
            }
            finally
            {
                if (_activeCts.TryRemove(task.Id, out var removedCts))
                {
                    removedCts.Dispose();
                }

                _queueSignal.Release();
                _ = SaveStateAsync();
            }
        });
    }

    public async Task SaveStateAsync()
    {
        try
        {
            List<DownloadTask> tasksCopy;
            lock (_taskLock)
            {
                tasksCopy = _tasks.ToList();
            }

            var filePath = _options.StateFilePath;
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = filePath + ".tmp";
            await using (var stream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, tasksCopy, JsonOptions);
                await stream.FlushAsync();
            }

            File.Move(tempFilePath, filePath, overwrite: true);
        }
        catch
        {
            // Suppress non-critical persistence failures
        }
    }

    public async Task LoadStateAsync()
    {
        try
        {
            var filePath = _options.StateFilePath;
            if (!File.Exists(filePath)) return;

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var loadedTasks = await JsonSerializer.DeserializeAsync<List<DownloadTask>>(stream, JsonOptions);

            if (loadedTasks != null)
            {
                lock (_taskLock)
                {
                    _tasks.Clear();
                    foreach (var task in loadedTasks)
                    {
                        // Any task that was active when app closed should be marked Paused
                        if (task.Status == DownloadStatus.Downloading)
                        {
                            task.Status = DownloadStatus.Paused;
                        }
                        task.SpeedBytesPerSec = 0;
                        task.EstimatedTimeRemaining = null;
                        task.RecalculateDownloadedBytes();
                        _tasks.Add(task);
                    }
                }
            }
        }
        catch
        {
            // If json is corrupted or unreadable, ignore
        }
    }

    public void Dispose()
    {
        _dispatcherCts?.Cancel();
        _dispatcherCts?.Dispose();
        _queueSignal.Dispose();

        foreach (var cts in _activeCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _activeCts.Clear();

        if (_ownsEngine)
        {
            _engine.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
