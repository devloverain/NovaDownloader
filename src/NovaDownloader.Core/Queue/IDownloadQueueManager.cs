using NovaDownloader.Core.Configuration;
using NovaDownloader.Core.Models;

namespace NovaDownloader.Core.Queue;

public interface IDownloadQueueManager : IDisposable
{
    DownloaderOptions Options { get; }
    IReadOnlyList<DownloadTask> Tasks { get; }
    int MaxConcurrentDownloads { get; set; }
    bool IsRunning { get; }

    void RefreshNetworkClient();

    event EventHandler<DownloadTask>? TaskAdded;
    event EventHandler<DownloadTask>? TaskStatusChanged;
    event EventHandler<DownloadProgressEventArgs>? TaskProgressUpdated;
    event EventHandler<DownloadTask>? TaskCompleted;
    event EventHandler<(DownloadTask Task, Exception Error)>? TaskFailed;
    event EventHandler<DownloadTask>? TaskRemoved;

    Task<DownloadTask> AddTaskAsync(string url, string destinationPath, string? fileName = null, CancellationToken cancellationToken = default);
    Task<DownloadTask> AddTaskAsync(string url, string destinationPath, string? fileName, bool startImmediately, string queueId = "main", CancellationToken cancellationToken = default);
    void AddTask(DownloadTask task);
    Task PauseTaskAsync(Guid taskId);
    Task ResumeTaskAsync(Guid taskId);
    Task CancelTaskAsync(Guid taskId);
    Task RemoveTaskAsync(Guid taskId, bool deleteFile = false);
    Task RemoveTasksAsync(IEnumerable<Guid> taskIds, bool deleteFile = false);

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task SaveStateAsync();
    Task LoadStateAsync();
}
