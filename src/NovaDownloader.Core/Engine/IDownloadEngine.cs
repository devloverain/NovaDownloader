using NovaDownloader.Core.Models;

namespace NovaDownloader.Core.Engine;

public interface IDownloadEngine : IDisposable
{
    event EventHandler<DownloadProgressEventArgs>? ProgressUpdated;
    event EventHandler<DownloadTask>? TaskCompleted;
    event EventHandler<(DownloadTask Task, Exception Error)>? TaskFailed;

    Task<ServerProbeResult> ProbeAsync(string url, CancellationToken cancellationToken = default);

    Task InitializeTaskAsync(DownloadTask task, CancellationToken cancellationToken = default);

    Task DownloadAsync(DownloadTask task, CancellationToken cancellationToken = default);
}
