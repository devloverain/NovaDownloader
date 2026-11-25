namespace NovaDownloader.Core.Models;

public enum DownloadStatus
{
    Pending,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}
