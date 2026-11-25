using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using NovaDownloader.Core.Models;
using NovaDownloader.Core.Queue;
using NovaDownloader.UI.Models;

namespace NovaDownloader.UI.Services;

/// <summary>
/// Manages named download queues and time-based automatic scheduling.
/// </summary>
public class QueueSchedulerService : IDisposable
{
    private static readonly string SchedulesFilePath = Path.Combine(
        UserSettingsService.AppDataDirectory,
        "schedules.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDownloadQueueManager _queueManager;
    private readonly Func<DownloadTask, Task> _startExternalTaskAsync;
    private readonly Action<string>? _onStatus;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _dispatchLock = new(1, 1);

    public ObservableCollection<ScheduleQueue> Queues { get; } = [];

    public event EventHandler? QueuesChanged;
    public event EventHandler<DownloadTask>? ScheduledTaskStarted;

    public QueueSchedulerService(
        IDownloadQueueManager queueManager,
        Func<DownloadTask, Task> startExternalTaskAsync,
        Action<string>? onStatus = null)
    {
        _queueManager = queueManager;
        _startExternalTaskAsync = startExternalTaskAsync;
        _onStatus = onStatus;

        Load();

        _queueManager.TaskStatusChanged += OnTaskStateChanged;
        _queueManager.TaskCompleted += OnTaskCompletedOrFailed;
        _queueManager.TaskFailed += OnTaskFailed;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnSchedulerTick;
        _timer.Start();
    }

    public ScheduleQueue GetQueueOrDefault(string? queueId)
    {
        if (!string.IsNullOrWhiteSpace(queueId))
        {
            var match = Queues.FirstOrDefault(q => string.Equals(q.Id, queueId, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return Queues.First(q => q.IsDefault);
    }

    public ScheduleQueue CreateQueue(string name, int maxConcurrent = 1)
    {
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Custom Schedule" : name.Trim();
        var slug = new string(cleanName.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c)).ToArray());
        if (string.IsNullOrWhiteSpace(slug)) slug = "queue";

        var uniqueId = slug;
        int suffix = 2;
        while (Queues.Any(q => string.Equals(q.Id, uniqueId, StringComparison.OrdinalIgnoreCase)))
        {
            uniqueId = $"{slug}_{suffix++}";
        }

        var queue = new ScheduleQueue
        {
            Id = uniqueId,
            Name = cleanName,
            MaxConcurrentDownloads = Math.Clamp(maxConcurrent, 1, 10)
        };

        Queues.Add(queue);
        Save();
        QueuesChanged?.Invoke(this, EventArgs.Empty);
        return queue;
    }

    public bool DeleteQueue(string queueId)
    {
        var queue = Queues.FirstOrDefault(q => string.Equals(q.Id, queueId, StringComparison.OrdinalIgnoreCase));
        if (queue == null || queue.IsDefault) return false;

        queue.IsRunning = false;
        foreach (var task in _queueManager.Tasks.Where(t => string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase)))
        {
            task.QueueId = ScheduleQueue.MainQueueId;
        }

        Queues.Remove(queue);
        Save();
        _ = _queueManager.SaveStateAsync();
        QueuesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void AssignTasksToQueue(IEnumerable<DownloadTask> tasks, string queueId)
    {
        var target = GetQueueOrDefault(queueId);
        int count = 0;
        foreach (var task in tasks)
        {
            task.QueueId = target.Id;
            count++;
        }

        if (count > 0)
        {
            _ = _queueManager.SaveStateAsync();
            _onStatus?.Invoke($"Assigned {count} download(s) to '{target.Name}'.");
        }
    }

    public async Task StartQueueAsync(string queueId)
    {
        var queue = GetQueueOrDefault(queueId);
        queue.IsRunning = true;

        if (!_queueManager.IsRunning)
            await _queueManager.StartAsync();

        await DispatchQueueAsync(queue);
        _onStatus?.Invoke($"Started schedule '{queue.Name}' (max {queue.MaxConcurrentDownloads} concurrent).");
    }

    public async Task StopQueueAsync(string queueId)
    {
        var queue = GetQueueOrDefault(queueId);
        queue.IsRunning = false;

        var activeTasks = _queueManager.Tasks
            .Where(t => string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase)
                     && (t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending))
            .ToList();

        foreach (var task in activeTasks)
        {
            if (task.IsExternalManaged)
                YtDlpService.PauseDownload(task.Id);
            await _queueManager.PauseTaskAsync(task.Id);
        }

        _onStatus?.Invoke($"Stopped schedule '{queue.Name}'.");
    }

    public async Task StartAllWaitingQueuesAsync()
    {
        foreach (var q in Queues.ToList())
        {
            var hasWaiting = _queueManager.Tasks.Any(t =>
                string.Equals(t.QueueId, q.Id, StringComparison.OrdinalIgnoreCase)
                && t.Status == DownloadStatus.Paused);

            if (hasWaiting || q.IsDefault)
                await StartQueueAsync(q.Id);
        }
    }

    public async Task StopAllRunningQueuesAsync()
    {
        foreach (var q in Queues.Where(q => q.IsRunning).ToList())
        {
            await StopQueueAsync(q.Id);
        }
    }

    private void OnTaskStateChanged(object? sender, DownloadTask task)
    {
        if (task.Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled)
        {
            TryAdvanceQueueForTask(task);
        }
    }

    private void OnTaskCompletedOrFailed(object? sender, DownloadTask task)
    {
        TryAdvanceQueueForTask(task);
    }

    private void OnTaskFailed(object? sender, (DownloadTask Task, Exception Error) tuple)
    {
        TryAdvanceQueueForTask(tuple.Task);
    }

    private void TryAdvanceQueueForTask(DownloadTask task)
    {
        var queue = Queues.FirstOrDefault(q => string.Equals(q.Id, task.QueueId, StringComparison.OrdinalIgnoreCase));
        if (queue is { IsRunning: true })
        {
            Application.Current?.Dispatcher.BeginInvoke(async () => await DispatchQueueAsync(queue));
        }
    }

    private async Task DispatchQueueAsync(ScheduleQueue queue)
    {
        await _dispatchLock.WaitAsync();
        try
        {
            if (!queue.IsRunning) return;

            var queueTasks = _queueManager.Tasks
                .Where(t => string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var activeCount = queueTasks.Count(t =>
                t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending);

            var availableSlots = queue.MaxConcurrentDownloads - activeCount;
            if (availableSlots > 0)
            {
                var candidates = queueTasks
                    .Where(t => t.Status == DownloadStatus.Paused)
                    .Take(availableSlots)
                    .ToList();

                foreach (var task in candidates)
                {
                    if (task.IsExternalManaged || YtDlpService.IsSupportedVideoPage(task.Url) || task.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        await _startExternalTaskAsync(task);
                    }
                    else
                    {
                        await _queueManager.ResumeTaskAsync(task.Id);
                    }

                    ScheduledTaskStarted?.Invoke(this, task);
                }
            }

            // If nothing is active or waiting in this queue anymore, mark queue idle
            var remainingActiveOrWaiting = _queueManager.Tasks.Any(t =>
                string.Equals(t.QueueId, queue.Id, StringComparison.OrdinalIgnoreCase)
                && (t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending || t.Status == DownloadStatus.Paused));

            if (!remainingActiveOrWaiting)
            {
                queue.IsRunning = false;
            }
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    private async void OnSchedulerTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        var minuteKey = now.ToString("yyyy-MM-dd HH:mm");

        foreach (var queue in Queues.Where(q => q.IsScheduleEnabled).ToList())
        {
            if (ShouldTriggerAtMinute(queue, queue.StartTime, now)
                && queue.LastStartTriggerKey != minuteKey)
            {
                queue.LastStartTriggerKey = minuteKey;

                if (queue.Frequency == ScheduleFrequency.OneTime)
                {
                    queue.IsScheduleEnabled = false;
                    Save();
                }

                await StartQueueAsync(queue.Id);
            }

            if (queue.HasStopTime
                && queue.IsRunning
                && ShouldTriggerAtMinute(queue, queue.StopTime, now)
                && queue.LastStopTriggerKey != minuteKey)
            {
                queue.LastStopTriggerKey = minuteKey;
                await StopQueueAsync(queue.Id);
            }
        }
    }

    private static bool ShouldTriggerAtMinute(ScheduleQueue queue, TimeSpan targetTime, DateTime now)
    {
        if (now.Hour != targetTime.Hours || now.Minute != targetTime.Minutes)
            return false;

        return queue.Frequency switch
        {
            ScheduleFrequency.OneTime => (queue.OneTimeDate?.Date ?? now.Date) == now.Date,
            ScheduleFrequency.SelectedDays => queue.DaysOfWeek.Contains(now.DayOfWeek),
            _ => true
        };
    }

    public void Load()
    {
        Queues.Clear();
        try
        {
            if (File.Exists(SchedulesFilePath))
            {
                var json = File.ReadAllText(SchedulesFilePath);
                var loaded = JsonSerializer.Deserialize<List<ScheduleQueue>>(json, JsonOptions);
                if (loaded != null)
                {
                    foreach (var q in loaded)
                    {
                        q.IsRunning = false;
                        Queues.Add(q);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed schedules file
        }

        if (!Queues.Any(q => q.IsDefault))
        {
            Queues.Insert(0, new ScheduleQueue
            {
                Id = ScheduleQueue.MainQueueId,
                Name = "Main Queue",
                MaxConcurrentDownloads = 1
            });
            Save();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(UserSettingsService.AppDataDirectory);
            var json = JsonSerializer.Serialize(Queues.ToList(), JsonOptions);
            File.WriteAllText(SchedulesFilePath, json);
        }
        catch
        {
            // Ignore save failures
        }

        QueuesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _timer.Stop();
        _queueManager.TaskStatusChanged -= OnTaskStateChanged;
        _queueManager.TaskCompleted -= OnTaskCompletedOrFailed;
        _queueManager.TaskFailed -= OnTaskFailed;
        _dispatchLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
