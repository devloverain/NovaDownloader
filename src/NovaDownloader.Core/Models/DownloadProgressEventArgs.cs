namespace NovaDownloader.Core.Models;

public class DownloadProgressEventArgs : EventArgs
{
    public required Guid TaskId { get; init; }
    public required long DownloadedBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required double ProgressPercentage { get; init; }
    public required double SpeedBytesPerSec { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public int ActiveSegmentsCount { get; init; }
}
